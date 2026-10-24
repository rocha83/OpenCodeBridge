using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>Veredito do breaker de loop agêntico.</summary>
public enum BreakerVerdict
{
    /// <summary>Nada a fazer: histórico sem sequência de falhas.</summary>
    None,

    /// <summary>Aviso no último resultado (2ª..Nª tentativa): manda trocar de estratégia.</summary>
    Nudge,

    /// <summary>Curto-circuito: não chama o modelo, devolve o aviso ao cliente.</summary>
    Trip
}

/// <summary>Resultado da análise do histórico de tools.</summary>
/// <param name="Verdict">Ação a tomar.</param>
/// <param name="Streak">Falhas consecutivas no fim do histórico.</param>
/// <param name="DistinctTargets">Alvos normalizados distintos nessa cauda.</param>
/// <param name="Notice">Texto do aviso (vazio quando <see cref="BreakerVerdict.None"/>).</param>
public sealed record BreakerResult(BreakerVerdict Verdict, int Streak, int DistinctTargets, string Notice);

/// <summary>
/// Detecta loop agêntico de tool: falhas consecutivas no fim do histórico em que
/// o modelo reescreve o MESMO alvo com pequenas mutações (proj-teste-001 →
/// proj-teste-999, opencodebridge → opencodedbidge). O anti-repeat textual não
/// pega esse padrão: cada iteração gera texto novo, então nada "repete".
/// A bridge é stateless por request; a leitura é do histórico dentro do próprio
/// corpo (function_call + function_call_output / tool), que o cliente reenvia.
/// </summary>
public static class ToolLoopBreaker
{
    /// <summary>Marcadores que caracterizam resultado de falha de tool.</summary>
    private static readonly Regex FailureMarkers = new(
        "exited with code [1-9]" +
        "|file not found|no such file|not a directory|is a directory" +
        "|search path does not exist|unable to find files|unable to write" +
        "|command not found|permission denied|operation not permitted" +
        "|arquivo ou diret[oó]rio inexistente|comando n[aã]o encontrado|permiss[aã]o negada" +
        "|n[aã]o encontrad[oa]|inexistente" +
        "|error de sintaxe|erro de sintaxe|syntaxerror|syntax error" +
        "|fatal:|unknown revision|does not match any file" +
        "|the user declined|tool call declined|not authorized|nao autorizado" +
        "|here-document .* delimitado",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Aviso injetado no último resultado (modo Nudge).</summary>
    public const string NudgeNotice =
        "\n\n[BRIDGE-BREAKER] Detectei {N} falhas consecutivas no MESMO alvo. PARE de reescrever o nome/caminho de memória. " +
        "Proximo passo obrigatorio: (1) rodar `ls -la <diretorio-pai>` e copiar o nome REAL da saida; " +
        "(2) se o diretorio nao existir, criar com caminho absoluto e conferir com `ls`; " +
        "(3) se ainda falhar, encerre e relate ao usuario o que tentou e o erro exato, sem nova tentativa.";

    /// <summary>Aviso devolvido no curto-circuito (modo Trip).</summary>
    public const string TripNotice =
        "[BRIDGE-BREAKER] Turno encerrado pela bridge: {N} falhas consecutivas no mesmo alvo, sem mudanca de estrategia. " +
        "Nao houve chamada ao modelo. Relate ao usuario o erro exato e a ultima saida de tool; " +
        "para prosseguir, comece a proxima mensagem validando o caminho com `ls -la` e usando o nome copiado da saida.";

    /// <summary>
    /// Analisa o histórico de mensagens (formato chat: assistant.tool_calls + tool
    /// results) e decide se há loop de falha.
    /// </summary>
    /// <param name="messages">Mensagens já convertidas para o formato chat.</param>
    /// <param name="soft">Falhas consecutivas que disparam o aviso.</param>
    /// <param name="hard">Falhas consecutivas que encerram o turno.</param>
    /// <returns>Veredito + métricas da cauda.</returns>
    public static BreakerResult Analyze(JsonArray? messages, int soft = 3, int hard = 6)
    {
        if (messages is null || messages.Count == 0 || soft <= 0) return new BreakerResult(BreakerVerdict.None, 0, 0, "");
        var calls = CollectCalls(messages);
        var results = CollectResults(messages, calls);
        // Cauda de falhas: só o fim conta (o modelo pode ter acertado antes).
        int streak = 0;
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var tools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = results.Count - 1; i >= 0; i--)
        {
            var r = results[i];
            if (!LooksLikeFailure(r.Content)) break;
            streak++;
            targets.Add(NormalizeTarget(r.Tool, r.Arguments));
            if (r.Tool.Length > 0) tools.Add(r.Tool);
        }
        if (streak < soft) return new BreakerResult(BreakerVerdict.None, streak, targets.Count, "");
        if (streak >= hard && hard > 0)
            return new BreakerResult(BreakerVerdict.Trip, streak, targets.Count, TripNotice.Replace("{N}", streak.ToString()));
        // Nudge só quando é claramente o MESMO alvo (mutações cosméticas) e no
        // máximo 2 tools distintas; falhas em alvos diferentes são progresso.
        if (targets.Count <= 1 && tools.Count <= 2)
            return new BreakerResult(BreakerVerdict.Nudge, streak, targets.Count, NudgeNotice.Replace("{N}", streak.ToString()));
        return new BreakerResult(BreakerVerdict.None, streak, targets.Count, "");
    }

    /// <summary>
    /// Anexa o aviso ao último resultado de tool (mantém os papéis intactos, o que
    /// o template de chat exige). Devolve true quando anexou.
    /// </summary>
    /// <param name="messages">Mensagens no formato chat (mutadas).</param>
    /// <param name="notice">Texto do aviso.</param>
    /// <returns>True = aviso anexado.</returns>
    public static bool ApplyNudge(JsonArray? messages, string notice)
    {
        if (messages is null || string.IsNullOrEmpty(notice)) return false;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject mo) continue;
            if (mo["role"]?.GetValue<string>() != "tool") continue;
            switch (mo["content"])
            {
                case JsonValue v when v.TryGetValue<string>(out string? text):
                    mo["content"] = text + notice;
                    return true;
                case JsonArray parts:
                {
                    for (int p = parts.Count - 1; p >= 0; p--)
                        if (parts[p] is JsonObject po && po["text"] is JsonValue pv && pv.TryGetValue<string>(out string? ptext))
                        { po["text"] = ptext + notice; return true; }
                    parts.Add(new JsonObject { ["type"] = "text", ["text"] = notice.TrimStart() });
                    return true;
                }
            }
            return false;
        }
        return false;
    }

    /// <summary>
    /// Chave de alvo normalizada: sem dígitos, sem separadores e em minúsculas.
    /// proj-teste-001 e proj-teste-999 caem na MESMA chave (é o padrão de loop).
    /// </summary>
    /// <param name="tool">Nome da tool.</param>
    /// <param name="arguments">Argumentos JSON crus.</param>
    /// <returns>Chave de comparação.</returns>
    public static string NormalizeTarget(string tool, string arguments)
    {
        string raw = arguments ?? "";
        try
        {
            if (JsonNode.Parse(raw) is JsonObject o)
            {
                // Path/comando são os campos que carregam o alvo real.
                foreach (var key in new[] { "path", "filePath", "command", "pattern", "oldString" })
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out string? s) && s.Length > 0) { raw = s; break; }
            }
        }
        catch { /* argumento inválido: usa o cru */ }
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.ToLowerInvariant())
        {
            if (char.IsDigit(c)) continue;
            if (c is '-' or '_' or ' ' or '\\' or '/') continue;
            sb.Append(c);
        }
        return (tool ?? "").ToLowerInvariant() + "|" + sb;
    }

    /// <summary>O conteúdo do resultado de tool indica falha?</summary>
    /// <param name="content">Texto do resultado.</param>
    /// <returns>True = falha reconhecida.</returns>
    public static bool LooksLikeFailure(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return FailureMarkers.IsMatch(content);
    }

    /// <summary>Chamadas de tool do histórico: call_id -> (tool, argumentos).</summary>
    private static Dictionary<string, (string Tool, string Arguments)> CollectCalls(JsonArray messages)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var node in messages)
        {
            if (node is not JsonObject mo) continue;
            if (mo["tool_calls"] is not JsonArray arr) continue;
            foreach (var c in arr)
            {
                if (c is not JsonObject co) continue;
                string id = co["id"]?.GetValue<string>() ?? "";
                string name = co["function"]?["name"]?.GetValue<string>() ?? "";
                string args = co["function"]?["arguments"] is JsonValue av && av.TryGetValue<string>(out string? a)
                    ? a
                    : (co["function"]?["arguments"]?.ToJsonString() ?? "");
                if (id.Length > 0) map[id] = (name, args);
            }
        }
        return map;
    }

    /// <summary>Resultados de tool na ordem do histórico, já ligados à chamada.</summary>
    private static List<(string Tool, string Arguments, string Content)> CollectResults(
        JsonArray messages, Dictionary<string, (string Tool, string Arguments)> calls)
    {
        var list = new List<(string, string, string)>();
        foreach (var node in messages)
        {
            if (node is not JsonObject mo) continue;
            if (mo["role"]?.GetValue<string>() != "tool") continue;
            string id = mo["tool_call_id"]?.GetValue<string>() ?? "";
            string content = mo["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out string? s) => s,
                JsonArray parts => string.Join("\n", parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
                _ => ""
            };
            calls.TryGetValue(id, out var call);
            list.Add((call.Tool ?? "", call.Arguments ?? "", content));
        }
        return list;
    }
}
