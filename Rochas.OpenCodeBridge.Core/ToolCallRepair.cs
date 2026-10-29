using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Reparo do turno quando o upstream recusa a tool call (500 de parse JSON ou args
/// truncado pela bridge). Unico lugar da mecanica: tira do historico a assistant
/// cujo args e' JSON invalido — o llama devolve 500 ao reler e o mesmo erro se
/// repetiria para sempre — e devolve UM nudge com a regra de JSON.
/// Consumidores (teste E2E, belt, Web) chamam aqui; nao reimplementam o texto.
/// </summary>
public static class ToolCallRepair
{
    /// <summary>
    /// Corpo do nudge de reparo (regra JSON + arquivo grande). Curto de proposito: o
    /// alvo e' o parser, nao a sintaxe da linguagem do artefato. O envelope
    /// (<c>BRIDGE (escopo):</c>) vem de <see cref="BridgeNotice"/>.
    /// </summary>
    public const string Nudge =
        "A tool call anterior veio com JSON invalido/truncado e o parser recusou o turno. " +
        "Reenvie UMA tool call valida: dentro do conteudo use `\\n` (barra + n) para quebra de linha e `\\\"` para aspas, " +
        "sem quebras cruas; se o arquivo for grande, grave um `write` curto e complete com `edit`.";

    /// <summary>
    /// Aviso do salvage para as tools recusadas (ex.: <c>BRIDGE (edit, write): ...</c>).
    /// </summary>
    /// <param name="tools">Nomes das calls com args quebrados.</param>
    /// <returns>Texto do aviso no formato unico da bridge.</returns>
    public static string Notice(IEnumerable<string>? tools) =>
        BridgeNotice.Notice(tools is null ? null : string.Join(", ", tools), Nudge);

    /// <summary>
    /// O conteudo e' SO o aviso de salvage (nenhuma call aproveitavel)? Nesse
    /// turno o modelo nao raciocinou sobre o problema — o cliente recebeu uma
    /// instrucao —, entao o consumidor nao deve cobrar isso do orcamento de turnos.
    /// </summary>
    /// <remarks>Predicado unico: teste/belt nao comparam texto de aviso por conta propria.</remarks>
    /// <param name="content">Conteudo da assistant recebida.</param>
    /// <returns>True quando o turno e' apenas o nudge do salvage.</returns>
    public static bool IsSalvageNotice(string? content) =>
        BridgeNotice.Is(content) && content!.Contains(Nudge, StringComparison.Ordinal);

    /// <summary>O args e' JSON completo e parseavel?</summary>
    /// <param name="args">Texto dos argumentos da tool call.</param>
    /// <returns>True quando fecha e parseia.</returns>
    public static bool ArgsOk(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return false;
        try { return JsonNode.Parse(args) is not null; }
        catch { return false; }
    }

    /// <summary>O tool_call do historico tem algum args invalido/truncado?</summary>
    /// <param name="message">Mensagem chat da assistant.</param>
    /// <returns>True quando ha ao menos um args que nao parseia.</returns>
    public static bool HasBrokenArgs(JsonObject message)
    {
        if (message["tool_calls"] is not JsonArray calls) return false;
        foreach (var c in calls)
            if (!ArgsOk(c?["function"]?["arguments"]?.GetValue<string>()))
                return true;
        return false;
    }

    /// <summary>Remove a ultima assistant cujo tool_call tem args invalido.</summary>
    /// <param name="history">Historico no formato chat (mutado).</param>
    /// <returns>True quando removeu alguma mensagem.</returns>
    public static bool DropBrokenAssistant(JsonArray history)
    {
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i] is not JsonObject o) continue;
            if (o["role"]?.GetValue<string>() != "assistant") continue;
            if (HasBrokenArgs(o)) { history.RemoveAt(i); return true; }
            return false; // a ultima assistant esta limpa: nao ha o que podar
        }
        return false;
    }

    /// <summary>Aplica o reparo: poda a assistant quebrada e anexa o aviso.</summary>
    /// <param name="history">Historico no formato chat (mutado).</param>
    /// <param name="tools">Nomes das calls quebradas (so' para a tag do aviso).</param>
    /// <returns>True quando havia assistant quebrada (o contexto mudou de fato).</returns>
    public static bool Apply(JsonArray history, IEnumerable<string>? tools = null)
    {
        bool dropped = DropBrokenAssistant(history);
        BridgeNotice.Append(history, Notice(tools));
        return dropped;
    }
}
