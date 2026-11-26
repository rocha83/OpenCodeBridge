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
    /// Cabecalho estavel do nudge de reparo (o consumidor reconhece o aviso por ele).
    /// Os EXEMPLOS ficam em <see cref="Examples"/> e o help curado por tool vem de
    /// <see cref="ToolHelp"/> — nada de texto de schema duplicado aqui.
    /// </summary>
    public const string Nudge =
        "A tool call anterior veio com JSON invalido/truncado e o parser recusou o turno. " +
        "Reenvie UMA tool call valida, com o JSON COMPLETO e fechado: quebra de linha dentro do " +
        "conteudo e' `\\n` (barra + n), nunca quebra crua; aspas dentro do conteudo sao `\\\"`.";

    /// <summary>
    /// Exemplos VALIDOS de argumentos (JSON completo, fecha e parseia). O modelo copia
    /// o literal do aviso, entao cada um e' verificado pelo pin (`JsonNode.Parse`).
    /// </summary>
    public static readonly string[] Examples =
    {
        """{"path":"Program.cs","content":"using System;\n\nclass Program { static void Main() { Console.WriteLine(\"ok\"); } }"}""",
        """{"path":"Program.cs","oldString":"Console.WriteLine(\"ok\");","newString":"Console.WriteLine(15);\nConsole.WriteLine(25);"}""",
        """{"command":"dotnet run --project mcs/mcs.csproj"}""",
    };

    /// <summary>
    /// Corpo do aviso: cabecalho + exemplos numerados + help curado das tools que
    /// quebraram (mesmo texto do schema). Sem nome de tool, cita o help de arquivo.
    /// </summary>
    /// <param name="tools">Nomes das calls recusadas (pode vir vazio).</param>
    /// <returns>Texto do corpo (sem a tag da bridge).</returns>
    static string Body(IEnumerable<string>? tools)
    {
        var sb = new System.Text.StringBuilder(Nudge);
        sb.Append("\nEXEMPLOS DE ARGS VALIDOS (copie a forma):");
        for (int i = 0; i < Examples.Length; i++)
            sb.Append("\n  ").Append(i + 1).Append(") ").Append(Examples[i]);
        string docs = ToolHelp.For(tools);
        if (docs.Length == 0) docs = ToolHelp.For(new[] { "write", "edit", "read", "shell" });
        if (docs.Length > 0) sb.Append("\n\n").Append(docs);
        return sb.ToString();
    }

    /// <summary>
    /// Aviso do salvage para as tools recusadas (ex.: <c>BRIDGE (edit, write): ...</c>).
    /// </summary>
    /// <param name="tools">Nomes das calls com args quebrados.</param>
    /// <returns>Texto do aviso no formato unico da bridge.</returns>
    public static string Notice(IEnumerable<string>? tools)
    {
        var list = tools?.ToList();
        return BridgeNotice.Notice(list is null || list.Count == 0 ? null : string.Join(", ", list), Body(list));
    }

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

    /// <summary>Parte de uma tool call capturada no stream (antes do reparo).</summary>
    /// <param name="Id">Id da call no protocolo.</param>
    /// <param name="Name">Nome da tool.</param>
    /// <param name="Args">Argumentos crus acumulados.</param>
    public readonly record struct SalvageCall(string Id, string Name, string Args);

    /// <summary>
    /// Resultado do salvage: calls aproveitaveis, nomes recusados, texto final
    /// (com o aviso quando houve args quebrado) e o finish_reason da resposta.
    /// </summary>
    /// <param name="Kept">Calls com nome e args JSON completos.</param>
    /// <param name="Rejected">Nomes das calls com args invalidos/truncados.</param>
    /// <param name="Content">Texto da assistant, com o aviso anexado quando necessario.</param>
    /// <param name="FinishReason">"tool_calls" quando ha calls aproveitaveis; "stop" quando nao ha.</param>
    public sealed record SalvagePlan(
        IReadOnlyList<SalvageCall> Kept,
        IReadOnlyList<string> Rejected,
        string Content,
        string FinishReason);

    /// <summary>
    /// Salvage de resposta parcial: entra toda call com nome e args JSON parseavel;
    /// args truncado vira aviso canonico (nunca tool call — o cliente reenvia e o
    /// llama responde 500 no parse). Unica mecanica para abort de stream e para o
    /// limite de execucao dos loops (chat e Responses).
    /// </summary>
    /// <param name="calls">Calls capturadas (stream ou acumulador).</param>
    /// <param name="content">Texto ja acumulado da assistant.</param>
    /// <returns>Plano de resposta ja montado.</returns>
    public static SalvagePlan Salvage(IEnumerable<SalvageCall> calls, string content)
    {
        var kept = new List<SalvageCall>();
        var rejected = new List<string>();
        foreach (var c in calls)
        {
            if (string.IsNullOrEmpty(c.Name)) continue;
            if (!ArgsOk(c.Args)) { rejected.Add(c.Name); continue; }
            kept.Add(c);
        }
        string body = content;
        if (rejected.Count > 0)
            body = (body.Length > 0 ? body + "\n\n" : "") + Notice(rejected);
        return new SalvagePlan(kept, rejected, body, kept.Count > 0 ? "tool_calls" : "stop");
    }

    /// <summary>Salvage de uma unica call (fronteira de execucao dos loops).</summary>
    /// <param name="id">Id da call.</param>
    /// <param name="name">Nome da tool.</param>
    /// <param name="args">Argumentos crus.</param>
    /// <param name="content">Texto da assistant (vazio na fronteira de execucao).</param>
    /// <returns>Plano com a call em <see cref="SalvagePlan.Kept"/> ou o aviso em <see cref="SalvagePlan.Content"/>.</returns>
    public static SalvagePlan Salvage(string id, string name, string args, string content = "") =>
        Salvage(new[] { new SalvageCall(id, name, args) }, content);
}
