using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>Veredito do controle de loop agentico.</summary>
public enum BreakerVerdict
{
    /// <summary>Nada a fazer: historico sem sequencia de falhas.</summary>
    None,

    /// <summary>Aviso no ultimo resultado: manda trocar de estrategia.</summary>
    Nudge,

    /// <summary>Curto-circuito: nao chama o modelo, devolve o aviso ao cliente.</summary>
    Trip
}

/// <summary>Resultado da analise do historico de tools.</summary>
/// <param name="Verdict">Acao a tomar.</param>
/// <param name="Streak">Falhas consecutivas no fim do historico.</param>
/// <param name="DistinctTargets">Alvos normalizados distintos nessa cauda.</param>
/// <param name="Notice">Texto do aviso (vazio quando <see cref="BreakerVerdict.None"/>).</param>
public sealed record BreakerResult(BreakerVerdict Verdict, int Streak, int DistinctTargets, string Notice);

/// <summary>
/// Classe-mae do escopo de TOOLS: contem o trabalho de ciclo de chamada —
/// identidade da chamada (<see cref="Signature"/>), alvo normalizado, deteccao de
/// falha, leitura do historico, avaliacao do loop por falha e da chamada repetida.
/// </summary>
/// <remarks>
/// A bridge e stateless por request: a leitura e do historico dentro do proprio
/// corpo (function_call + function_call_output / tool), que o cliente reenvia.
/// Separar de <see cref="ContentAntiRepeat"/> importa porque o anti-repeat textual
/// NAO ve este padrao: cada iteracao gera texto novo (proj-teste-001 -> 999).
/// Os helpers abaixo sao finos: so declaram escopo/limiares e delegam para aqui.
/// </remarks>
public abstract class ToolsAntiRepeat
{
    /// <summary>O que este controle mede (vai para o log).</summary>
    public abstract string Scope { get; }

    /// <summary>Aviso injetado no ultimo resultado (modo Nudge).</summary>
    public const string NudgeNotice =
        "\n\n[BRIDGE-BREAKER] Detectei {N} falhas consecutivas no MESMO alvo. PARE de reescrever o nome/caminho de memoria. " +
        "Proximo passo obrigatorio: (1) rodar `ls -la <diretorio-pai>` e copiar o nome REAL da saida; " +
        "(2) se o diretorio nao existir, criar com caminho absoluto e conferir com `ls`; " +
        "(3) se ainda falhar, encerre e relate ao usuario o que tentou e o erro exato, sem nova tentativa.";

    /// <summary>Aviso devolvido no curto-circuito (modo Trip).</summary>
    public const string TripNotice =
        "[BRIDGE-BREAKER] Turno encerrado pela bridge: {N} falhas consecutivas no mesmo alvo, sem mudanca de estrategia. " +
        "Nao houve chamada ao modelo. Relate ao usuario o erro exato e a ultima saida de tool; " +
        "para prosseguir, comece a proxima mensagem validando o caminho com `ls -la` e usando o nome copiado da saida.";

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

    /// <summary>Assinatura canonica de uma chamada ("nome(args)").</summary>
    /// <param name="name">Nome da tool.</param>
    /// <param name="arguments">Argumentos crus.</param>
    /// <returns>Assinatura normalizada.</returns>
    public static string Signature(string? name, string? arguments)
        => ContentAntiRepeat.Normalize((name ?? "").Trim().ToLowerInvariant()
            + "(" + ContentAntiRepeat.Normalize(arguments ?? "") + ")");

    /// <summary>
    /// Chave de alvo normalizada: sem digitos, sem separadores e em minusculas.
    /// proj-teste-001 e proj-teste-999 caem na MESMA chave (padrao de loop).
    /// </summary>
    /// <param name="tool">Nome da tool.</param>
    /// <param name="arguments">Argumentos JSON crus.</param>
    /// <returns>Chave de comparacao.</returns>
    public static string NormalizeTarget(string? tool, string? arguments)
    {
        string raw = arguments ?? "";
        try
        {
            if (JsonNode.Parse(raw) is JsonObject o)
            {
                // Path/comando sao os campos que carregam o alvo real.
                foreach (var key in new[] { "path", "filePath", "command", "pattern", "oldString" })
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out string? s) && s.Length > 0) { raw = s; break; }
            }
        }
        catch { /* argumento invalido: usa o cru */ }
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.ToLowerInvariant())
        {
            if (char.IsDigit(c)) continue;
            if (c is '-' or '_' or ' ' or '\\' or '/') continue;
            sb.Append(c);
        }
        return (tool ?? "").ToLowerInvariant() + "|" + sb;
    }

    /// <summary>O conteudo do resultado de tool indica falha?</summary>
    /// <param name="content">Texto do resultado.</param>
    /// <returns>True = falha reconhecida.</returns>
    public static bool LooksLikeFailure(string? content)
        => !string.IsNullOrWhiteSpace(content) && FailureMarkers.IsMatch(content);

    /// <summary>Chamadas de tool do historico: call_id -> (tool, argumentos).</summary>
    /// <param name="messages">Mensagens no formato chat.</param>
    /// <returns>Mapa por call_id.</returns>
    protected static Dictionary<string, (string Tool, string Arguments)> CollectCalls(JsonArray messages)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var node in messages)
        {
            if (node is not JsonObject mo || mo["tool_calls"] is not JsonArray arr) continue;
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

    /// <summary>Resultados de tool na ordem do historico, ja ligados a chamada.</summary>
    /// <param name="messages">Mensagens no formato chat.</param>
    /// <param name="calls">Mapa devolvido por <see cref="CollectCalls"/>.</param>
    /// <returns>Lista (tool, args, conteudo).</returns>
    protected static List<(string Tool, string Arguments, string Content)> CollectResults(
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

    /// <summary>
    /// Loop por FALHA: falhas consecutivas no fim do historico em que o modelo
    /// reescreve o MESMO alvo com mutacoes cosmeticas.
    /// </summary>
    /// <param name="messages">Mensagens no formato chat.</param>
    /// <param name="soft">Falhas que disparam o aviso (0 desliga).</param>
    /// <param name="hard">Falhas que encerram o turno (0 desliga).</param>
    /// <returns>Veredito + metricas da cauda.</returns>
    protected BreakerResult EvaluateFailureLoop(JsonArray? messages, int soft, int hard)
    {
        if (messages is null || messages.Count == 0 || soft <= 0)
            return new BreakerResult(BreakerVerdict.None, 0, 0, "");
        var calls = CollectCalls(messages);
        var results = CollectResults(messages, calls);
        // Cauda de falhas: so o fim conta (o modelo pode ter acertado antes).
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
        // Nudge so quando e claramente o MESMO alvo (mutacoes cosmeticas) e no
        // maximo 2 tools distintas; falhas em alvos diferentes sao progresso.
        if (targets.Count <= 1 && tools.Count <= 2)
            return new BreakerResult(BreakerVerdict.Nudge, streak, targets.Count, NudgeNotice.Replace("{N}", streak.ToString()));
        return new BreakerResult(BreakerVerdict.None, streak, targets.Count, "");
    }

    /// <summary>
    /// Anexa o aviso ao ultimo resultado de tool (mantem os papeis intactos, o que
    /// o template de chat exige).
    /// </summary>
    /// <param name="messages">Mensagens no formato chat (mutadas).</param>
    /// <param name="notice">Texto do aviso.</param>
    /// <returns>True = aviso anexado.</returns>
    public bool ApplyNudge(JsonArray? messages, string notice)
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
    /// Chamada REPETIDA: assinaturas que ja apareceram N vezes no historico.
    /// </summary>
    /// <param name="messages">Mensagens no formato chat.</param>
    /// <param name="minCount">Ocorrencias que caracterizam repeticao (>= 2).</param>
    /// <returns>Conjunto de assinaturas loopadas.</returns>
    protected HashSet<string> FindRepeatedCalls(JsonNode? messages, int minCount)
    {
        int min = Math.Max(2, minCount);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (messages is JsonArray arr)
            foreach (var m in arr)
            {
                if (m is not JsonObject mo || mo["tool_calls"] is not JsonArray calls) continue;
                foreach (var c in calls)
                {
                    if (c is not JsonObject co || co["function"] is not JsonObject fn) continue;
                    string sig = Signature(fn["name"]?.GetValue<string>() ?? "",
                                           fn["arguments"]?.GetValue<string>() ?? "");
                    if (sig == "()") continue;
                    counts[sig] = counts.TryGetValue(sig, out int n) ? n + 1 : 1;
                }
            }
        return new HashSet<string>(counts.Where(kv => kv.Value >= min).Select(kv => kv.Key),
                                   StringComparer.Ordinal);
    }
}

/// <summary>
/// Helper fino de identidade: expoe a assinatura/alvo/falha da classe-mae com os
/// nomes usados pela politica de risco e pelos testes. Nao tem regra propria.
/// </summary>
public sealed class ToolSig : ToolsAntiRepeat
{
    /// <inheritdoc />
    public override string Scope => "tool-signature";

    /// <summary>Assinatura canonica ("nome(args)").</summary>
    /// <param name="name">Nome da tool.</param>
    /// <param name="arguments">Argumentos crus.</param>
    /// <returns>Assinatura normalizada.</returns>
    public static string Of(string? name, string? arguments) => Signature(name, arguments);

    /// <summary>Alvo normalizado (sem digitos/separadores).</summary>
    /// <param name="tool">Nome da tool.</param>
    /// <param name="arguments">Argumentos JSON crus.</param>
    /// <returns>Chave de comparacao.</returns>
    public static string Target(string? tool, string? arguments) => NormalizeTarget(tool, arguments);

    /// <summary>O resultado de tool indica falha?</summary>
    /// <param name="content">Texto do resultado.</param>
    /// <returns>True = falha reconhecida.</returns>
    public static bool IsFailure(string? content) => LooksLikeFailure(content);
}

/// <summary>Helper fino do loop por falha: so carrega os limiares soft/hard.</summary>
public sealed class ToolsBreaker : ToolsAntiRepeat
{
    /// <summary>Falhas consecutivas que disparam o aviso.</summary>
    public int Soft { get; }

    /// <summary>Falhas consecutivas que encerram o turno (0 desliga o trip).</summary>
    public int Hard { get; }

    /// <summary>Cria o controle com os limiares da CLI/appsettings.</summary>
    /// <param name="soft">Falhas para o aviso (0 desliga).</param>
    /// <param name="hard">Falhas para o curto-circuito (0 desliga).</param>
    public ToolsBreaker(int soft, int hard)
    {
        Soft = soft;
        Hard = hard;
    }

    /// <inheritdoc />
    public override string Scope => "failure-loop";

    /// <summary>Avalia o historico com os limiares deste helper.</summary>
    /// <param name="messages">Mensagens no formato chat.</param>
    /// <returns>Veredito + metricas da cauda.</returns>
    public BreakerResult Evaluate(JsonArray? messages) => EvaluateFailureLoop(messages, Soft, Hard);
}

/// <summary>Helper fino da chamada repetida: so carrega o numero minimo de ocorrencias.</summary>
public sealed class RepeatedCall : ToolsAntiRepeat
{
    /// <summary>Ocorrencias da mesma assinatura que caracterizam repeticao.</summary>
    public int MinCount { get; }

    /// <summary>Cria o controle de chamada repetida.</summary>
    /// <param name="minCount">Ocorrencias no historico (>= 2).</param>
    public RepeatedCall(int minCount) => MinCount = Math.Max(2, minCount);

    /// <inheritdoc />
    public override string Scope => "repeated-call";

    /// <summary>Assinaturas que ja se repetiram no historico.</summary>
    /// <param name="messages">Mensagens no formato chat.</param>
    /// <returns>Conjunto de assinaturas loopadas.</returns>
    public HashSet<string> Find(JsonNode? messages) => FindRepeatedCalls(messages, MinCount);
}
