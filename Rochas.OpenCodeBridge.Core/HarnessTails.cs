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

    public const string DefaultTools =
        "MODO DE USO DAS TOOLS (complemento ao harness; NAO repete regra de comportamento): " +
        "CONCATENACAO DE COMANDOS: usar `&&` só é permitido dentro da tool shell. " +
        "SHELL: um comando por chamada; nunca quebre linha com escape literal; para script, write + `bash <arquivo>`; `cd` NAO persiste entre chamadas — use `cd <dir> && <cmd>` na mesma call. " +
        "GLOB: nao tem alternancia nem regex — use `**/*.{cs,js,ts,json}` ou chame glob duas vezes; se retornar 0 matches ou falhar, use `ls` (ou `find`) via shell como fallback antes de concluir que o arquivo nao existe. " +
        "DATA: se o enunciado pedir a data de hoje, rode `date` (ou `date +%F`) e copie a saida; nunca invente data de memoria. " +
        "EDIT: `oldString` e um trecho exato ja existente; `newString` substitui SO esse trecho. Para ACRESCENTAR uma marca, o `newString` DEVE comecar pelo `oldString` e adicionar a marca depois; nunca substitua a linha inteira pela marca. Se o pedido mandar editar/marcar um arquivo existente, use `edit` (nao `write`) e confirme com `read`/`grep`. " +
        "`mkdir X && cd Y` so vale com X e Y identicos. " +
        "GIT: a tool git aceita `path` = diretorio do repo; sem `path` ela roda na raiz do workspace e falha quando o repo esta numa subpasta (use `path` em init/add/commit/log, ou `git -C <dir>` na shell). `git init <dir>` SEMPRE com o diretorio explicito (nunca no cwd do workspace); o repo nasce na pasta do projeto do enunciado; confirme com `git -C <dir> rev-parse --show-toplevel`. IDENTIDADE: se `commit` falhar com 'Author identity unknown'/'Please tell me who you are', chame a tool git DUAS vezes, com `path` do repo: `config user.name <nome>` e depois `config user.email <email>` (sem `&&`, sem `--global`); repita o commit. Uma suboperacao por chamada na tool git (init, depois add, depois commit); nao encadeie `&&` nela (para cadeia use shell). Se vier 'unknown option'/'usage:', leia a sintaxe e nao repita a chamada. " +
        "DOTNET: com mais de um projeto na pasta, `--project <pasta>` e ambiguo ('contains more than one project file'): aponte o ARQUIVO (`<dir>/<nome>.fsproj` ou `.csproj`). " +
        "F#: o entrypoint e `module Program` em Program.fs; o assembly e o nome do .fsproj. " +
        "PROJETO EXISTENTE: edite os arquivos do projeto; `dotnet new` so em pasta vazia (criar segundo .csproj/.fsproj na mesma pasta quebra `dotnet run --project <pasta>`: 'contem mais de um arquivo de projeto').";

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
        // Data do host no proprio harness: modelos pequenos inventam data de
        // memoria; o pedido "hoje" passa a ter uma fonte verdadeira no contexto.
        string hoje = "DATA ATUAL DO HOST: " + System.DateTime.Now.ToString("yyyy-MM-dd") +
                      " (use esta data quando o pedido citar hoje).";
        t = (t.Length > 0 ? t + "\n\n" : "") + hoje;
        if (!string.IsNullOrWhiteSpace(behavior)) t = (t.Length > 0 ? t + "\n\n" : "") + behavior;
        if (!string.IsNullOrWhiteSpace(tools)) t = (t.Length > 0 ? t + "\n\n" : "") + tools;
        return t;
    }
}
