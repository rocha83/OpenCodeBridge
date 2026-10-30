// Portao generico de fluxo bridge (PENDINGS P1): um lugar so', executado pelo
// runner para QUALQUER teste E2E em modo bridge.

using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Confere, no registro que a PROPRIA bridge fez do request efetivamente enviado
/// (<c>GET /api/test/last-system</c>), se o fluxo em modo bridge se completou:
/// main harness aplicado, caudas ativas e (quando o request declarou <c>tools</c>)
/// a cauda de tools no system final.
/// A fonte e' o texto final do request — nao o system que o teste monta, nao a
/// flag de CLI — entao a recusa e' por INSTRUMENTACAO ausente e nunca por
/// assertiva semantica. Em modo opencode o portao nao roda (o cliente se
/// auto-gerencia).
/// </summary>
internal static class BridgeFlowGate
{
    /// <summary>
    /// Devolve vazio quando o fluxo esta' completo; senao a CAUSA faltante
    /// (<c>ausente main_harness</c>, <c>ausente tails</c>, <c>ausente tools_tail</c>).
    /// </summary>
    /// <param name="record">Registro de <c>system-sent</c> da bridge (ou null).</param>
    /// <param name="testStartMs">Inicio do teste (epoch ms); 0 desliga a checagem de idade.</param>
    /// <returns>Causa da faltante, "" quando o fluxo fecha, ou <c>INCONCLUSIVO: ...</c>.</returns>
    public static string Check(JsonObject? record, long testStartMs = 0)
    {
        if (record is null || Str(record, "event") != "system-sent")
            return "nenhum system do request registrado na bridge (GET /api/test/last-system vazio)";
        // Registro velho = o teste nao chegou a mandar request (ou a bridge reiniciou):
        // acusar falta de harness ali seria mentira.
        long ts = Num(record, "ts");
        if (testStartMs > 0 && ts > 0 && ts < testStartMs)
            return "registro anterior ao teste (nenhum request deste teste chegou a bridge)";
        // Registro de OUTRO cliente (raia opencode) nao prova nada sobre o teste: a
        // bridge guarda so' o ultimo system, e um cliente concorrente pode sobrescrever.
        string lane = Str(record, "lane");
        if (lane.Length > 0 && lane != "bridge")
            return "INCONCLUSIVO: registro de outro cliente (lane=" + lane + ")";
        if (!IsTrue(record, "main_harness"))
            return "ausente main_harness";
        string scope = Str(record, "tail_scope") is { Length: > 0 } s ? s : "off";
        if (!IsTrue(record, "harness_tails"))
            return $"ausente tails (harness_tails={Json(record, "harness_tails")} tail_scope={scope})";
        if (scope == "off")
            return "ausente tails (tail_scope=off)";
        // So quando o request declarou tools: sem tools a cauda sai de proposito
        // (senao o modelo devolve o JSON da tool como texto e trunca).
        if (IsTrue(record, "has_tools") && !IsTrue(record, "tools_tail"))
            return "ausente tools_tail (request declara tools e o system final nao traz a cauda)";
        return "";
    }

    /// <summary>Registro numa linha, para o log (evidencia do que o request trazia).</summary>
    /// <param name="record">Registro de <c>system-sent</c>.</param>
    /// <returns>path/modelo/main_harness/tails/tools.</returns>
    public static string Describe(JsonObject record)
        => $"path={S(record, "path")} model={S(record, "model")} chars={S(record, "chars")} "
         + $"main_harness={S(record, "main_harness")} has_tools={S(record, "has_tools")} "
         + $"tools_tail={S(record, "tools_tail")} tail_scope={S(record, "tail_scope")} "
         + $"harness_tails={S(record, "harness_tails")} fsmini={S(record, "fsmini")} lane={S(record, "lane")}";

    /// <summary>
    /// Busca o registro na bridge e aplica o <see cref="Check"/>.
    /// </summary>
    /// <param name="bridge">Base da bridge alvo.</param>
    /// <param name="testStartMs">Inicio do teste (epoch ms) para descartar registro velho.</param>
    /// <returns>(causa faltante, dump do registro).</returns>
    public static (string Missing, string Dump) Probe(string bridge, long testStartMs = 0)
    {
        JsonObject? rec;
        try { rec = TestContext.GetObj(bridge + "/api/test/last-system").GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            return ("registro indisponivel: " + ex.Message.Split('\n')[0], "sem last-system");
        }
        return (Check(rec, testStartMs), rec is null ? "registro vazio" : Describe(rec));
    }

    /// <summary>
    /// Probe que sabe se o teste mandou request para ESTA bridge: o contador
    /// <c>model_requests</c> nao subiu no run ⇒ nao ha fluxo a conferir aqui (fase
    /// que usa bridge secundaria, ex. Q3 4125 na edicao do front, ou teste so' de
    /// leitura como <c>E-front-verify</c>). O registro velho da rodada anterior
    /// deixa de ser FAIL: era falso negativo — medido em 2026-10-30 (front-bridge
    /// FAIL em 34s com o painel de edicao todo verde).
    /// </summary>
    /// <param name="bridge">Base da bridge alvo.</param>
    /// <param name="testStartMs">Inicio do teste (epoch ms).</param>
    /// <param name="modelRequestsBefore">model_requests lido ANTES do teste.</param>
    /// <returns>(causa faltante ou INCONCLUSIVO, dump).</returns>
    public static (string Missing, string Dump) Probe(string bridge, long testStartMs, long modelRequestsBefore)
    {
        long after = ModelRequests(bridge);
        if (after <= modelRequestsBefore)
            return ("INCONCLUSIVO: nenhum request de modelo nesta bridge durante o teste (nada a conferir)",
                $"model_requests {modelRequestsBefore} -> {after}");
        return Probe(bridge, testStartMs);
    }

    /// <summary>Contador de requests de modelo da bridge (0 quando indisponivel).</summary>
    /// <param name="bridge">Base da bridge alvo.</param>
    /// <returns>model_requests da vida do processo.</returns>
    public static long ModelRequests(string bridge)
    {
        try
        {
            JsonObject? m = TestContext.GetObj(bridge + "/api/metrics").GetAwaiter().GetResult();
            return m?["model_requests"]?.GetValue<long>() ?? 0;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Campo numerico (0 quando ausente/nao-numero).</summary>
    /// <param name="o">Registro.</param>
    /// <param name="key">Chave.</param>
    /// <returns>Valor numerico.</returns>
    static long Num(JsonObject o, string key)
        => o[key] is JsonValue v && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Integer,
               System.Globalization.CultureInfo.InvariantCulture, out long n) ? n : 0;

    /// <summary>True so quando o campo e' booleano verdadeiro (a bridge emite bool de verdade).</summary>
    /// <param name="o">Registro.</param>
    /// <param name="key">Chave.</param>
    /// <returns>Valor booleano verdadeiro, ou false (ausente/nao-bool).</returns>
    static bool IsTrue(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<bool>(out bool b) && b;

    /// <summary>Campo string (vazio quando ausente/nao-string).</summary>
    /// <param name="o">Registro.</param>
    /// <param name="key">Chave.</param>
    /// <returns>Valor string ou "".</returns>
    static string Str(JsonObject o, string key)
        => o[key] is JsonValue v && v.TryGetValue<string>(out string? s) ? s : "";

    static string S(JsonObject o, string key)
    {
        JsonNode? n = o[key];
        if (n is null) return "";
        if (n is JsonValue v && v.TryGetValue<string>(out string? s)) return s;
        return n.ToJsonString();
    }

    static string Json(JsonObject o, string key) => o[key]?.ToJsonString() ?? "n/d";
}
