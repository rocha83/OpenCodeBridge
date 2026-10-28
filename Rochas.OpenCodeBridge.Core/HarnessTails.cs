namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Caudas de inducao do harness. Os textos abaixo sao fallback do codigo; o
/// deployment ajusta em appsettings (harnessBehaviorTail/harnessToolsTail) ou
/// CLI (--harness-behavior-tail/--harness-tools-tail). O nucleo base
/// (MainHarness) e hardcoded e nao passa por aqui.
/// </summary>
public static class HarnessTails
{
    /// <summary>
    /// Delta de comportamento da bridge (o nucleo ja mora no MainHarness).
    /// Mantido curto de proposito: so o que e especifico deste canal/workspace.
    /// </summary>
    public const string DefaultBehavior =
        "COMPORTAMENTO (delta da bridge; a base ja cobre postura, caminhos e erros): " +
        "Formato: em conversa use Unicode (setas, <=, pi, somatorio) em vez de LaTeX; use $...$ so em bloco de codigo ou formula, onde o cliente renderiza. " +
        "TOOLS PRIMEIRO: em enunciado que pede execucao, a PRIMEIRA saida e a tool call; ZERO linha de prosa antes do primeiro disparo e nenhum texto entre uma call e a proxima. " +
        "Excecao: no maximo 1 linha entre calls dependentes, so para indicar o proximo passo. " +
        "So escreva ao usuario depois da ultima acao, com evidencia real (trecho de log, saida de comando). " +
        "PERSISTENCIA: cada resultado de tool e insumo da proxima call no MESMO turno; nao espere 'siga' e nao encerre com 'vou fazer', 'iniciando' ou confirmacao. " +
        "PRIORIDADE: se o master mandar explicar antes de comando nao trivial, execute primeiro e mostre a saida; a explicacao, se necessaria, vem depois, em 1 linha.";

    /// <summary>
    /// Variante do delta para pedido SEM tools. A cauda de tools e a linha
    /// "TOOLS PRIMEIRO" empurram o modelo a disparar tool call inexistente
    /// (ele devolve o JSON da chamada como TEXTO e trunca a resposta). Em
    /// pedido de texto/plano a cauda vira so formato.
    /// </summary>
    public const string DefaultBehaviorNoTools =
        "COMPORTAMENTO (delta da bridge; a base ja cobre postura, caminhos e erros): " +
        "Formato: em conversa use Unicode (setas, <=, pi, somatorio) em vez de LaTeX; use $...$ so em bloco de codigo ou formula, onde o cliente renderiza. " +
        "PEDIDO SEM TOOLS: responda em TEXTO com o CODIGO/PRODUTO COMPLETO pedido (no corpo da mensagem). " +
        "NAO entregue 'plano de implementacao', 'plano arquitetural', 'estrutura do projeto', sumario nem indice de passos: o texto deve ser o entregavel final. " +
        "NAO elida codigo: proibido '// ...', '/* ... */', 'resto igual' ou comentario no lugar do corpo — escreva cada metodo/trecho por inteiro. " +
        "Quando o pedido terminar com 'ENTREGUE O ARTEFATO COMPLETO EM TEXTO, SEM TOOL CALLS' (ou 'MOSTRE-ME COMO MODO PLANO EM TEXTO'), " +
        "entregue o ARTEFATO FINAL no corpo; NAO responda com documento de plano, 'plano de implementacao' nem 'aguardando confirmacao'.";

    public const string DefaultTools =
        "MODO DE USO DAS TOOLS (regras transversais; o contrato de cada tool vai no schema): " +
        "Prefira a tool dedicada: read/write/edit para arquivo, glob/grep para descobrir, webfetch/websearch para rede, shell para executar. " +
        "Um passo = uma acao; argumentos JSON completos; leia o resultado antes de repetir. " +
        "Independentes no mesmo turno; dependentes esperam. Nao repita a mesma chamada/caminho; em loop, mude a abordagem. " +
        "`execute` nao e sandbox: a bridge desembrulha tools.X; bloqueio de role nao se contorna por shell. " +
        "DOTNET: com mais de um projeto na pasta, `--project <pasta>` e ambiguo: aponte o ARQUIVO (`<dir>/<nome>.fsproj` ou `.csproj`). " +
        "ARQUIVO: pedido que manda criar/gravar arquivo entrega via `write` (conteudo completo); devolver o codigo em prosa no chat NAO conclui o pedido. " +
        "PROJETO EXISTENTE: edite os arquivos do projeto; `dotnet new` so em pasta vazia (segundo .csproj/.fsproj na mesma pasta quebra `dotnet run --project <pasta>`). " +
        "SUBAGENTE: o schema de cada tool viaja no array `tools` do request; preserve o array e o system do role ao delegar; nao troque schema por prosa.";

    /// <summary>
    /// Mini tutorial F#: injetado apenas na fase F# (nao entra no DefaultTools,
    /// para nao impor sintaxe F# em requests C#/Orch).
    /// </summary>
    public const string FsMini =
        "F# MINI (sintaxe; os exemplos sao genericos, nao o algoritmo do pedido): " +
        "arquivo: 1a linha `module Program`; funcoes `let nome (x: float) = ...`. " +
        "acumulador: `let mutable acc = 0.0` e `acc <- acc + x`; loop `for i in 1 .. n-1 do ...` com `float i`. " +
        "listas: `List.fold (fun acc x -> acc + x) 0.0 xs`; `List.sum xs`; `List.map (fun x -> x * x) xs`. " +
        "range: `[1 .. n-1]` e passo 1; com passo e `[start .. step .. finish]` (ex.: `[1 .. 2 .. n-1]` para impares) — " +
        "`[1; 3 .. n-1]` NAO existe (`;` separa elementos, nao passo) e os extremos precisam do mesmo tipo (int com int, float com float). " +
        "math: `System.Math.Sin x`; `System.Math.Sqrt x`; `System.Math.PI`; potencia `x ** 2.0`. " +
        "tipos: `%` so em int; converta com `float n` e `int x`. " +
        "saida: `printfn \"%.6f\" valor`. " +
        "tools: `write` grava o arquivo inteiro; `shell` roda `dotnet run --project <dir>/<nome>.fsproj`; apos erro, `read`/`edit` so na linha indicada.";

    /// <summary>
    /// Compõe o system prompt com as caudas. Com harness desligado devolve o
    /// texto original (tubo puro do OpenCode).
    /// </summary>
    /// <param name="system">System prompt original.</param>
    /// <param name="harness">Liga/desliga as caudas.</param>
    /// <param name="behavior">Cauda de comportamento (pode ser vazia).</param>
    /// <param name="tools">Cauda de tools (pode ser vazia).</param>
    /// <param name="hasTools">Request traz array `tools`? Sem tools a cauda de
    /// tools e a linha "TOOLS PRIMEIRO" saem (o modelo devolveria JSON de tool
    /// como texto).</param>
    /// <returns>System prompt composto.</returns>
    public static string Compose(string? system, bool harness, string? behavior, string? tools,
        bool hasTools = true)
    {
        if (!harness) return system ?? "";
        string t = (system ?? "").Trim();
        // Data do host no proprio harness: modelos pequenos inventam data de
        // memoria; o pedido "hoje" passa a ter uma fonte verdadeira no contexto.
        string hoje = "DATA ATUAL DO HOST: " + System.DateTime.Now.ToString("yyyy-MM-dd") +
                      " (use esta data quando o pedido citar hoje).";
        t = (t.Length > 0 ? t + "\n\n" : "") + hoje;
        // Pedido sem tools: troca o delta padrao (que manda disparar tool) pela
        // variante de texto. Override do deployment e preservado.
        string? beh = behavior;
        if (!hasTools && string.Equals(behavior, DefaultBehavior, StringComparison.Ordinal))
            beh = DefaultBehaviorNoTools;
        if (!string.IsNullOrWhiteSpace(beh)) t = (t.Length > 0 ? t + "\n\n" : "") + beh;
        if (hasTools && !string.IsNullOrWhiteSpace(tools)) t = (t.Length > 0 ? t + "\n\n" : "") + tools;
        return t;
    }
}
