using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// Coercao model-agnostica de tool calls em texto (ex-DeepSeekToolCoercion,
// generalizada: Coder V2-Lite, Qwen2.5-Coder e outros que nao emitem
// tool_calls estruturadas devolvem a chamada como texto no content).
// Quando o turno volta sem tool_calls, esta coercao extrai as chamadas do
// texto e devolve o conteudo limpo (blocos parseados removidos).
// Padroes (ordem de tentativa):
//  1. hermes em texto: <tool_call>{"name":"...","arguments":{...}|"..."}</tool_call>
//  2. wrapper plural do coder: <tools>{"name":"...","arguments":{...}}</tools>
//  3. fence json: ```json {"name":"...","arguments":...} ```
//  4. tokens DeepSeek-V3: <｜tool▁calls▁begin｜>...function<｜tool▁sep｜>nome...args...<｜tool▁call▁end｜>
//  5. XML oficial DeepSeek (<tool_name><param>..</param></tool_name>, so knownTools).
public static class ToolsCoercion
{
    public const int MaxCalls = 8;

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

        string text = content;
        // <attempt_completion><result>...</result></attempt_completion>: turno
        // final — desembrulha e encerra (sem calls).
        var att = Regex.Match(text, @"<attempt_completion>\s*(?:<result>(.*?)</result>|(.*?))\s*</attempt_completion>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (att.Success)
        {
            string result = att.Groups[1].Success ? att.Groups[1].Value : att.Groups[2].Value;
            text = text.Replace(att.Value, "\n" + result + "\n");
            cleaned = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
            return calls;
        }
        text = ExtractHermes(text, calls);
        if (calls.Count < MaxCalls) text = ExtractToolsPlural(text, calls);
        if (calls.Count < MaxCalls) text = ExtractJsonFences(text, calls, known);
        if (calls.Count < MaxCalls) text = ExtractV3Tokens(text, calls);
        if (calls.Count < MaxCalls) text = ExtractXml(text, calls, known);

        if (calls.Count > 0)
            cleaned = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return calls;
    }

    private static string ExtractHermes(string text, List<BridgeHelper.ToolCall> calls)
    {
        foreach (Match m in Regex.Matches(text, @"<tool_call>\s*(\{.*?\})\s*</tool_call>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (calls.Count >= MaxCalls) break;
            if (TryBuildCall(m.Groups[1].Value, calls)) text = text.Replace(m.Value, "\n");
        }
        return text;
    }

    private static string ExtractToolsPlural(string text, List<BridgeHelper.ToolCall> calls)
    {
        // Qwen2.5-Coder via llama: embrulha no plural <tools>{...}</tools>
        // (o template poe as assinaturas em <tools> e o modelo ecoa o wrapper).
        foreach (Match m in Regex.Matches(text, @"<tools>\s*(\{.*?\})\s*</tools>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (calls.Count >= MaxCalls) break;
            if (TryBuildCall(m.Groups[1].Value, calls)) text = text.Replace(m.Value, "\n");
        }
        return text;
    }

    private static string ExtractJsonFences(string text, List<BridgeHelper.ToolCall> calls, List<string>? known)
    {
        foreach (Match m in Regex.Matches(text, "```(?:json)?[^\\n\\r]*[\\r\\n]+(.*?)```",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (calls.Count >= MaxCalls) break;
            string body = m.Groups[1].Value.Trim();
            if (!body.StartsWith("{")) continue;
            if (TryBuildCall(body, calls)) { text = text.Replace(m.Value, "\n"); continue; }
            // Variante sem "name": {"<tool>": {...args...}} (chave única = tool).
            if (known is not null && known.Count > 0)
            {
                JsonObject? obj;
                try { obj = JsonNode.Parse(body)?.AsObject(); }
                catch { continue; }
                if (obj is not null && obj.Count == 1)
                {
                    string key = obj.First().Key;
                    string canonical = known.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? "";
                    if (canonical.Length > 0 && obj[key] is JsonObject argsObj)
                    {
                        calls.Add(new BridgeHelper.ToolCall($"dsq-{calls.Count}", canonical, argsObj.ToJsonString(JsonRelaxed())));
                        text = text.Replace(m.Value, "\n");
                    }
                }
            }
        }
        return text;
    }

    private static string ExtractV3Tokens(string text, List<BridgeHelper.ToolCall> calls)
    {
        // <｜tool▁calls▁begin｜><｜tool▁call▁begin｜>function<｜tool▁sep｜>nome\n```json\nargs\n```<｜tool▁call▁end｜>...<｜tool▁calls▁end｜>
        foreach (Match outer in Regex.Matches(text, "<｜tool▁calls▁begin｜>(.*?)<｜tool▁calls▁end｜>",
                     RegexOptions.Singleline))
        {
            if (calls.Count >= MaxCalls) break;
            bool any = false;
            foreach (Match inner in Regex.Matches(outer.Groups[1].Value,
                         "<｜tool▁call▁begin｜>function<｜tool▁sep｜>(?<name>[^\\n\\r]+)[\\r\\n]+```(?:json)?[\\r\\n]+(?<args>.*?)```[\\r\\n]*<｜tool▁call▁end｜>",
                         RegexOptions.Singleline))
            {
                if (calls.Count >= MaxCalls) break;
                string name = inner.Groups["name"].Value.Trim();
                string args = inner.Groups["args"].Value.Trim();
                if (TryBuildCall($"{{\"name\": {JsonQuote(name)}, \"arguments\": {(StartsJson(args) ? args : JsonQuote(args))}}}", calls))
                    any = true;
            }
            if (any) text = text.Replace(outer.Value, "\n");
        }
        return text;
    }

    // Padrão oficial DeepSeek (doc function calling): <tool_name> com um
    // <param>valor</param> por argumento. Só aceita tags da lista anunciada
    // (knownTools) para não engolir XML qualquer do texto.
    private static string ExtractXml(string text, List<BridgeHelper.ToolCall> calls, List<string>? known)
    {
        if (known is null || known.Count == 0) return text;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "tool_call", "think", "thinking", "attempt_completion", "tool_response", "subthink" };
        foreach (Match m in Regex.Matches(text, @"<(?<tool>[A-Za-z_][A-Za-z0-9_\-\.]{0,63})>(?<body>.*?)</\k<tool>>",
                     RegexOptions.Singleline))
        {
            if (calls.Count >= MaxCalls) break;
            string tag = m.Groups["tool"].Value;
            if (skip.Contains(tag)) continue;
            string canonical = known.FirstOrDefault(k => k.Equals(tag, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (canonical.Length == 0) continue;
            var args = new JsonObject();
            foreach (Match p in Regex.Matches(m.Groups["body"].Value, @"<(?<p>[A-Za-z_][A-Za-z0-9_\-\.]{0,63})>(?<v>.*?)</\k<p>>",
                         RegexOptions.Singleline))
                args[p.Groups["p"].Value] = XmlUnescape(p.Groups["v"].Value.Trim());
            calls.Add(new BridgeHelper.ToolCall($"dsq-{calls.Count}", canonical, args.ToJsonString(JsonRelaxed())));
            text = text.Replace(m.Value, "\n");
        }
        return text;
    }

    private static string XmlUnescape(string s) => s
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
        .Replace("&apos;", "'").Replace("&amp;", "&");

    private static readonly JsonSerializerOptions RelaxedJson =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static JsonSerializerOptions JsonRelaxed() => RelaxedJson;

    private static bool TryBuildCall(string json, List<BridgeHelper.ToolCall> calls)
    {        JsonObject? obj;
        try { obj = JsonNode.Parse(json)?.AsObject(); }
        catch { return false; }
        if (obj is null) return false;
        // Aceita name/arguments (hermes) e function/parameters (variante DeepSeek).
        string name = obj["name"]?.GetValue<string>()
            ?? obj["function"]?.AsObject()?["name"]?.GetValue<string>()
            ?? obj["function"]?.GetValue<string>()
            ?? "";
        var argsNode = obj["arguments"] ?? obj["parameters"];
        if (!IsValidName(name)) return false;
        string args = argsNode switch
        {
            null => "{}",
            JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            _ => argsNode.ToJsonString(JsonRelaxed()),
        };
        calls.Add(new BridgeHelper.ToolCall($"dsq-{calls.Count}", name.Trim(), args));
        return true;
    }

    private static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(name.Trim(), @"^[A-Za-z_][A-Za-z0-9_\-\.]{0,63}$");

    private static bool StartsJson(string s)
    {
        s = s.TrimStart();
        return s.StartsWith("{") || s.StartsWith("[");
    }

    private static string JsonQuote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
