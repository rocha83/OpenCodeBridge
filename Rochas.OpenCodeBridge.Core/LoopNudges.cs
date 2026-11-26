namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Textos canonicos das intervecoes do loop agente. Fonte unica: consumidores
/// (Web, bridge, testes) anexam o literal devolvido aqui — nunca reescrevem a
/// mensagem. Segue a regra do projeto: a dica ensina a forma CERTA.
/// </summary>
public static class LoopNudges
{
    /// <summary>Cutucada de anti-giro por leitura sem producao (1/2 ou 2/2).</summary>
    public static string AntiGiro(int attempt) =>
        $"[Anti-giro {attempt}/2] Voce esta ha varios turnos so navegando (leitura) sem produzir texto nem delegar. " +
        "PARE de chamar tools de leitura: resuma o que ja achou em 3 linhas e delegue a proxima fase via subagent tool.";

    /// <summary>Nota honesta quando o anti-giro encerra sem saida.</summary>
    public const string AntiGiroStop =
        "Interrompido por giro em leitura: o agente navegou sem produzir texto nem delegar após 2 correções automáticas.";

    /// <summary>Cutucada para turno que so raciocinou (sem content e sem tool).</summary>
    public const string EmptyTurn =
        "[harness] responda em texto agora: o raciocínio não é exibido ao usuário.";

    /// <summary>Cutucada para prosa sem chamada de tool quando tools foram anunciadas.</summary>
    public static string ToolsCoerce(int attempt) =>
        $"[tools-coerce {attempt}/2] Você respondeu em prosa sem chamar tools. " +
        "As tools estão no seu contexto (bloco # Tools). Para agir, emita a chamada " +
        "no formato exato, sem prosa junto. Para delegar uma fase: " +
        "<tool_call>{\"name\": \"subagent\", \"arguments\": {\"prompt\": \"enunciado da fase\"}}</tool_call>";

    /// <summary>Recusa de subagent sem prompt (o tool_call_id tambem e' respondido).</summary>
    public const string SubagentEmptyPrompt =
        "[subagent] recusado: prompt vazio. Reenvie a fase com objetivo, arquivos e o que fazer.";

    /// <summary>Contencao de chamada identica repetida (ping-pong).</summary>
    public static string SameCall(string name, string args) =>
        "Aqui é a ponte de contenção OpenCodeBridge: você repetiu a mesma chamada da tool '" + name +
        "' 3 vezes seguidas com os mesmos argumentos (" + ToolLoopContext.Cut(args, 200) +
        "). Não repita: varie os argumentos, use outra tool ou conclua a tarefa com o que você já tem. " +
        "Responda com uma ação diferente.";
}
