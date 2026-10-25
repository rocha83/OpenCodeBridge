using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Invariante do loop agentivo, compartilhada por qualquer transporte:
/// <b>toda</b> tool chamada no turno N tem a resposta no contexto do turno N+1,
/// no shape do protocolo em uso — <c>chat/completions</c>
/// (<c>assistant.tool_calls</c> + <c>role=tool</c>/<c>tool_call_id</c>) ou
/// Responses (<c>function_call</c> + <c>function_call_output</c>/<c>call_id</c>).
/// </summary>
/// <remarks>
/// O harness nunca reescreve o resultado da tool no prompt: quem corrige e o
/// modelo, lendo o output que voltou. Consequencia pratica: um loop novo (ou
/// uma copia de loop) nao pode descartar o par call/resposta entre tentativas —
/// quando isso acontece o modelo "repete o mesmo erro" sem nunca ve-lo.
/// Centralizar shape + orcamento aqui elimina essa classe de regressao; use
/// <see cref="Unanswered"/> para provar, em teste, que nenhuma call ficou orfa.
/// </remarks>
public static class ToolLoopContext
{
    /// <summary>Orcamento default (chars) do output de tool que volta ao contexto.</summary>
    public const int DefaultBudget = 6000;

    /// <summary>
    /// Recorta o texto ao orcamento do contexto, deixando marcador com o total
    /// (o historico guarda o integral). <paramref name="max"/> &lt;= 0 desliga o corte.
    /// </summary>
    public static string Cut(string? text, int max = DefaultBudget)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (max <= 0 || text!.Length <= max) return text;
        return text[..max] + $"\n[...cortado: {text.Length} chars no contexto; integral no historico]";
    }

    /// <summary>Resposta de tool no shape <c>chat/completions</c>.</summary>
    public static JsonObject ChatTool(string? callId, string? output, int max = DefaultBudget) => new()
    {
        ["role"] = "tool",
        ["tool_call_id"] = callId ?? "",
        ["content"] = Cut(output, max),
    };

    /// <summary>Resposta de tool no shape Responses (<c>function_call_output</c>).</summary>
    public static JsonObject ResponsesOutput(string? callId, string? output, int max = DefaultBudget) => new()
    {
        ["type"] = "function_call_output",
        ["call_id"] = callId ?? "",
        ["output"] = Cut(output, max),
    };

    /// <summary>Ids de call que ja tem resposta no contexto (aceita os dois shapes).</summary>
    public static IReadOnlyList<string> AnsweredIds(JsonArray? messages)
    {
        var ids = new List<string>();
        if (messages is null) return ids;
        foreach (var m in messages)
        {
            if (m is not JsonObject o) continue;
            string id = o["role"]?.GetValue<string>() == "tool"
                ? o["tool_call_id"]?.GetValue<string>() ?? ""
                : o["type"]?.GetValue<string>() switch
                {
                    "function_call_output" or "custom_tool_call_output" or "shell_call_output"
                        => o["call_id"]?.GetValue<string>() ?? "",
                    _ => "",
                };
            if (id.Length > 0 && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Ids de <paramref name="callIds"/> que ainda nao tem resposta no contexto.
    /// Vazio = invariante satisfeita (nenhuma call orfa).
    /// </summary>
    public static IReadOnlyList<string> Unanswered(JsonArray? messages, IEnumerable<string>? callIds)
    {
        if (callIds is null) return Array.Empty<string>();
        var answered = AnsweredIds(messages);
        var missing = new List<string>();
        foreach (var id in callIds)
            if (!string.IsNullOrEmpty(id) && !answered.Contains(id)) missing.Add(id);
        return missing;
    }
}
