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
        "COMPORTAMENTO (delta da bridge): " +
        "Workspace: procure pastas/arquivos citados tambem um nivel abaixo e na arvore de subpastas, tanto do workspace quanto dos paths citados. " +
        "Formato: em conversa use Unicode (setas, <=, pi, somatorio) em vez de LaTeX; use $...$ so em bloco de codigo ou formula, onde o cliente renderiza. " +
        "TOOLS PRIMEIRO: em enunciado que pede execucao, a PRIMEIRA saida e a tool call; ZERO linha de prosa antes do primeiro disparo e nenhum texto entre uma call e a proxima. " +
        "Excecao: no maximo 1 linha entre calls dependentes, so para indicar o proximo passo. " +
        "So escreva ao usuario depois da ultima acao, com evidencia real (trecho de log, saida de comando). " +
        "Se falhar, leia o erro, tente outra abordagem; apos 2 tentativas, explique a dificuldade e peca uma sugestao de proximo passo. " +
        "PERSISTENCIA: cada resultado de tool e insumo da proxima call no MESMO turno; nao espere 'siga' e nao encerre com 'vou fazer', 'iniciando' ou confirmacao. " +
        "PRIORIDADE: se o master mandar explicar antes de comando nao trivial, execute primeiro e mostre a saida; a explicacao, se necessaria, vem depois, em 1 linha. " +
        "Nao se auto-apresente, nao peca confirmacao para o obvio; responda na lingua do usuario. " +
        "Resposta final curta: no maximo 2 frases, com o que foi feito e a evidencia.";

    /// <summary>Como usar as tools (sem repetir o que o grammar/canal nativo já garante).</summary>
    /// <remarks>Regra acrescentada em 2026-10-24 depois da analise de duas sessoes
    /// reais em que o modelo perdeu o turno: identificador corrompido na emissao
    /// (proj-teste-001 virou proj-teste-999, opencodebridge virou opencodedbidge),
    /// glob com alternancia "|", escape "\\n" literal no shell, comando simulado
    /// reportado como build OK e loop de 10 tentativas sem teste discriminante.</remarks>
    public const string DefaultTools =
        "MODO DE USO DAS TOOLS (complemento ao harness; NAO repete regra de comportamento): " +
        "SHELL: um comando por chamada; nunca quebre linha com escape literal; para script, write + `bash <arquivo>`; `cd` NAO persiste entre chamadas — use `cd <dir> && <cmd>` na mesma call. " +
        "GLOB: nao tem alternancia nem regex — use `**/*.{cs,js,ts,json}` ou chame glob duas vezes. " +
        "`mkdir X && cd Y` so vale com X e Y identicos. " +
        "GIT: `git init <dir>` SEMPRE com o diretorio explicito (nunca no cwd do workspace); o repo nasce na pasta do projeto do enunciado; confirme com `git -C <dir> rev-parse --show-toplevel`; identidade so local (`git -C <dir> config user.email/user.name`) — `--global` proibido; se vier 'unknown option'/'usage:', leia a sintaxe e nao repita a chamada. " +
        "DOTNET: com mais de um projeto na pasta, `--project <pasta>` e ambiguo ('contains more than one project file'): aponte o ARQUIVO (`<dir>/<nome>.fsproj` ou `.csproj`). " +
        "F#: o entrypoint e `module Program` em Program.fs; o assembly e o nome do .fsproj.";

    /// <summary>
    /// Compõe o system prompt com as caudas. Com harness desligado devolve o
    /// texto original (tubo puro do OpenCode).
    /// </summary>
    /// <param name="system">System prompt original.</param>
    /// <param name="harness">Liga/desliga as caudas.</param>
    /// <param name="behavior">Cauda de comportamento (pode ser vazia).</param>
    /// <param name="tools">Cauda de tools (pode ser vazia).</param>
    /// <returns>System prompt composto.</returns>
    public static string Compose(string? system, bool harness, string? behavior, string? tools)
    {
        if (!harness) return system ?? "";
        string t = (system ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(behavior)) t = (t.Length > 0 ? t + "\n\n" : "") + behavior;
        if (!string.IsNullOrWhiteSpace(tools)) t = (t.Length > 0 ? t + "\n\n" : "") + tools;
        return t;
    }
}
