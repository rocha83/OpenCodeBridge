using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// Orquestrador da coercao de tool calls em texto (content → tool_calls).
// Cada familia de modelo tem a sua classe com o nome do modelo:
//   - QwenToolCalls      hermes <tool_call>, plural <tools>, <function>
//   - DeepSeekToolCalls  fence json, tokens V3, XML <tool_name>, <attempt_completion>
//   - Core/GemmaToolCalls  <|tool_call>call:nome{...}<tool_call|> (template nativo)
// Aqui ficam so a allowlist/catalogo, o nome canonico e a montagem do ToolCall.
public static class ToolsCoercion
{
    public const int MaxCalls = 8;

    // Catálogo real (fallback quando o chamador não informa knownTools):
    // nome alucinado (echo, etc.) nunca vira call — fica como prosa e não
    // alimenta o ciclo erro->desculpa->loop do modelo.
    private static readonly HashSet<string> Catalog = new(StringComparer.OrdinalIgnoreCase)
        { "shell", "read", "grep", "glob", "fetch", "git", "subagent", "write", "edit" };

    // Apelidos da família shell que o modelo emite (canonizam p/ "shell").
    private static string CanonAlias(string name) => name.Trim().ToLowerInvariant() switch
    {
        "bash" or "sh" or "exec" or "proc" => "shell",
        _ => name.Trim(),
    };

    public static bool IsDeepSeek(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        model.Contains("deepseek", StringComparison.OrdinalIgnoreCase);

    // Qwen2.5-Coder (alias qwen3.5-9b esconde o nome: detecta pelo padrao
    // hermes/plural no texto, nao pelo id — ver Coerce).
    public static bool IsCoder(string? model) =>
        !string.IsNullOrWhiteSpace(model) &&
        model.Contains("coder", StringComparison.OrdinalIgnoreCase);

    // Nomes anunciados (formato OpenAI) p/ restringir o parse XML ao válido.
    public static List<string> NamesOf(System.Text.Json.Nodes.JsonArray? tools)
    {
        var names = new List<string>();
        if (tools is null) return names;
        foreach (var t in tools)
            if ((t as JsonObject)?["function"]?["name"]?.GetValue<string>() is string n
                && !string.IsNullOrWhiteSpace(n))
                names.Add(n);
        return names;
    }

    // Devolve as chamadas extraídas; cleaned = content sem os blocos parseados.
    // Model-agnostico: coage sempre que o texto parseia (o chamador ja garante
    // que tools foram pedidas); o id do modelo so documenta a origem.
    public static List<BridgeHelper.ToolCall> Coerce(string? model, string content, out string cleaned, IEnumerable<string>? knownTools = null)
    {
        cleaned = content ?? "";
        var calls = new List<BridgeHelper.ToolCall>();
        if (string.IsNullOrWhiteSpace(content)) return calls;
        var known = knownTools?.ToList();

        // Turno final do DeepSeek: <attempt_completion> desembrulha e encerra sem call.
        if (DeepSeekToolCalls.TryUnwrapAttemptCompletion(content, out string attempt))
        {
            cleaned = attempt;
            return calls;
        }

        bool Room() => calls.Count < MaxCalls;
        bool Try(string json) => TryBuildCall(json, calls, known);
        void Add(string name, string args) =>
            calls.Add(new BridgeHelper.ToolCall($"dsq-{calls.Count}", name, args));

        string text = QwenToolCalls.Extract(content, Try, Room);
        text = DeepSeekToolCalls.Extract(text, Try, Add, Room, known);

        if (calls.Count > 0)
            cleaned = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return calls;
    }

    private static readonly JsonSerializerOptions RelaxedJson =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonSerializerOptions JsonRelaxed() => RelaxedJson;

    private static bool TryBuildCall(string json, List<BridgeHelper.ToolCall> calls, List<string>? known)
    {        JsonObject? obj;
        try { obj = JsonNode.Parse(json)?.AsObject(); }
        catch { return false; }
        if (obj is null) return false;
        // Aceita name/arguments (hermes) e function/parameters (variante DeepSeek).
        string name = obj["name"]?.GetValue<string>()
            ?? obj["function"]?.AsObject()?["name"]?.GetValue<string>()
            ?? obj["function"]?.GetValue<string>()
            ?? "";
        // Prefixo alucinado do harness (default.shell): o registro usa nomes
        // simples; "default." nunca e tool valida — remove sem risco.
        if (name.StartsWith("default.", StringComparison.OrdinalIgnoreCase))
            name = name["default.".Length..];
        var argsNode = obj["arguments"] ?? obj["parameters"];
        if (!IsValidName(name)) return false;
        // Allowlist: com lista anunciada exige igualdade (canoniza a caixa);
        // sem lista, exige o catálogo real. Alucinada (echo) volta false e o
        // trecho fica como prosa (não vira call, não gera erro em loop).
        string trimmed = CanonAlias(name);
        string canonical = known is { Count: > 0 }
            ? known.FirstOrDefault(k => k.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                || (CanonAlias(k) == "shell" && trimmed == "shell")) ?? ""
            : Catalog.Contains(trimmed) ? trimmed : "";
        if (canonical.Length == 0) return false;
        string args = argsNode switch
        {
            null => "{}",
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            _ => argsNode.ToJsonString(JsonRelaxed()),
        };
        calls.Add(new BridgeHelper.ToolCall($"dsq-{calls.Count}", canonical, args));
        return true;
    }

    private static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(name.Trim(), @"^[A-Za-z_][A-Za-z0-9_\-\.]{0,63}$");
}
