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
        "MODO DE USO DAS TOOLS (delta da bridge): " +
        "CAMINHOS: ao citar pasta/arquivo fora do workspace use caminho ABSOLUTO e COPIE o nome da saida de ls/glob; nunca reescreva de memoria. " +
        "Se mkdir/read/edit/write/cd falhar com 'nao existe'/'not found', rode `ls -la <diretorio-pai>` e use o nome exato; nao tente variacao do mesmo nome. " +
        "`mkdir X && cd Y` so vale com X e Y identicos. " +
        "GLOB: nao tem alternancia nem regex — use `**/*.{cs,js,ts,json}` ou chame glob duas vezes. " +
        "SHELL: um comando por chamada; nunca quebre linha com escape literal; para script, write + `bash <arquivo>`; heredoc so se o role permitir, com `<<'EOF'` fechado. " +
        "GIT/ZIP: git init antes do primeiro add/commit; identidade via `git -c user.email=... -c user.name=...`; PROIBIDO `git config --global` (so local, via `git -C <dir> config`); `cd` NAO persiste entre chamadas: use `cd <dir> && <cmd>` na mesma call ou `git -C <dir>`; se pedir os dois, versione e depois empacote (sem bin/obj). " +
        "GIT-RAIZ: o repositorio nasce na pasta do projeto indicada no enunciado (ex.: teste-001), nunca no workspace inteiro por acidente; confirme com `git -C <dir> rev-parse --show-toplevel` antes do primeiro commit. " +
        "COMANDO INVALIDO: se o git responder 'unknown option' ou 'usage:', nao repita a mesma chamada; leia a sintaxe e use a forma correta (`git init` antes de config/add/commit; `git -C <dir> config user.email ...`). " +
        "EDIT/WRITE: alteracao pontual usa edit, sem reescrever o arquivo inteiro; write cria um arquivo por chamada e emite so o JSON da tool call, sem texto em volta. " +
        "PROJETO EXISTENTE: leia e edite os arquivos do projeto; nao recrie o que ja existe. " +
        "DOTNET RUN/BUILD: com mais de um projeto na pasta, `--project <pasta>` e ambiguo ('contains more than one project file'); aponte o ARQUIVO do projeto (`--project <dir>/<nome>.fsproj` ou `.csproj`). " +
        "F#: o entrypoint e `module Program` em Program.fs e o namespace/modulo NAO e o nome da classe como em C#; o assembly e o nome do .fsproj. " +
        "FASES: quando o enunciado pedir divisao/delegacao, use a tool de subagente e agregue o retorno. " +
        "BINARIO: .db/.sqlite/.png via shell+python3, nao read. REDE: webfetch/websearch, nao shell+curl. " +
        "NUNCA simule execucao: sem output real da tool, a acao esta NAO FEITA; nao relate sucesso. " +
        "PROIBIDO `sudo` sem ordem explicita e build/deploy em /usr/src (PROD). " +
        "ERRO: leia a mensagem, varie a abordagem (max 2) e ajuste o trecho exato; nao repita a mesma chamada/caminho. " +
        "NAO invente comando, tool ou caminho: confirme com `command -v`/`ls`. " +
        "Tool bloqueada para o role: explique que cabe a outro role, nao contorne por shell. " +
        "Pronto so com ACEITE executado e evidencia anexada.";

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
