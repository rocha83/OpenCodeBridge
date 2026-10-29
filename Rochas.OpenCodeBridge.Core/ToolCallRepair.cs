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
    /// Nudge de reparo (regra JSON + arquivo grande). Curto de proposito: o alvo e'
    /// o parser, nao a sintaxe da linguagem do artefato.
    /// </summary>
    public const string Nudge =
        "A tool call anterior veio com JSON invalido/truncado e o parser recusou o turno. " +
        "Reenvie UMA tool call valida: dentro do conteudo use `\\n` (barra + n) para quebra de linha e `\\\"` para aspas, " +
        "sem quebras cruas; se o arquivo for grande, grave um `write` curto e complete com `edit`.";

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

    /// <summary>Aplica o reparo: poda a assistant quebrada e anexa o nudge.</summary>
    /// <param name="history">Historico no formato chat (mutado).</param>
    /// <returns>True quando havia assistant quebrada (o contexto mudou de fato).</returns>
    public static bool Apply(JsonArray history)
    {
        bool dropped = DropBrokenAssistant(history);
        history.Add(new JsonObject { ["role"] = "user", ["content"] = Nudge });
        return dropped;
    }
}
