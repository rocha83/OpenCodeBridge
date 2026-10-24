namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Caudas de indução do harness. Fonte única do texto: o host console e a Web UI
/// usam os mesmos defaults; cada um pode sobrescrever em <see cref="BridgeOptions"/>.
/// </summary>
public static class HarnessTails
{
    /// <summary>Como se comportar (workspace, formato de conversa).</summary>
    public const string DefaultBehavior =
        "COMPORTAMENTO (workspace): quando o usuario informar pastas de atuacao fora do workspace, procure pastas ou arquivos citados tambem um nivel abaixo (pastas vizinhas) e na arvore de subpastas, tanto do workspace quanto dos paths citados. " +
        "COMPORTAMENTO (formato): em conversa escreva com Unicode (setas, menor-igual, pi, somatorio) em vez de LaTeX; use $...$ somente dentro de bloco de codigo ou de formula matematica, onde o cliente renderiza. " +
        // Paragrafo abaixo veio do harnessBehaviorTail que estava SÓ no appsettings do
        // PROD; trazido para a fonte unica do codigo (regra do repo: cauda mora aqui).
        "COMPORTAMENTO ESPERADO: (a) Um passo = uma acao: se falta tool, emita a call em vez de narrar a intencao. " +
        "(b) So escreva para o usuario DEPOIS da ultima acao, dizendo o que foi feito com a evidencia real (trecho de log, saida de comando). " +
        "(c) Se uma acao falha, nao repita a mesma: leia o erro, identifique a causa, tente outra abordagem; se 2 tentativas falharem, PARE e relate o que tentou e por onde travou. " +
        "(d) Nao anuncie passo futuro que voce nao vai executar agora. " +
        "(e) Nao se auto-apresente, nao peca confirmacao para o obvio, responda na lingua do usuario. " +
        "(f) Respostas curtas: no maximo 2 frases sem tool; com tool, so o que foi feito.";

    /// <summary>Como usar as tools (sem repetir o que o grammar/canal nativo já garante).</summary>
    /// <remarks>Regra acrescentada em 2026-10-24 depois da analise de duas sessoes
    /// reais em que o modelo perdeu o turno: identificador corrompido na emissao
    /// (proj-teste-001 virou proj-teste-999, opencodebridge virou opencodedbidge),
    /// glob com alternancia "|", escape "\\n" literal no shell, comando simulado
    /// reportado como build OK e loop de 10 tentativas sem teste discriminante.</remarks>
    public const string DefaultTools =
        "MODO DE USO DAS TOOLS: uma chamada por resposta; aguarde o resultado antes da proxima e nao narre a chamada no texto. " +
        "Arquivos: prefira write/edit com caminho relativo ao workspace; redirecionamento e pipes no shell vao via bash -c \"...\". " +
        "CAMINHOS (obrigatorio): ao citar pasta/arquivo de fora do workspace use caminho ABSOLUTO e COPIE o nome da saida de uma tool (ls/glob) na mesma mensagem; nunca reescreva o nome de memoria. " +
        "Se mkdir/read/edit/write/cd falhar com 'nao existe' ou 'not found', o proximo passo OBRIGATORIO e `ls -la <diretorio-pai>` e repetir com o nome exato que aparecer: nao tente de novo o mesmo nome nem uma variacao dele. " +
        "Um comando que encadeia `mkdir X && cd Y` so vale com X e Y identicos, escritos uma unica vez. " +
        "GLOB: nao tem alternancia nem regex — proibido `a|b`, `*a* | *b*` ou lista com virgula; use `**/*.{cs,js,ts,json}` ou chame glob duas vezes. " +
        "SHELL: um comando por chamada; proibido heredoc (`<<EOF`, `<<'PY'`); para script, escreva o arquivo com write e rode `bash <arquivo>`; nunca quebre linha com a sequencia literal \\n (use quebra real ou varias chamadas). " +
        "NUNCA simule execucao: proibido `echo \"build concluido\"`, `sleep` ou qualquer texto que finja saida de comando; sem output real da tool a acao esta NAO FEITA e voce nao pode relatar sucesso. " +
        "PROIBIDO `sudo` sem ordem explicita do usuario e proibido build/deploy em /usr/src (PROD). " +
        "Leia texto com read; arquivo binario (.db, .sqlite, .png) nao se le com read (use shell+python3). " +
        "Rede: use a tool fetch, nunca shell+curl. " +
        "Git e zip sao independentes: se o enunciado pedir os dois, versione primeiro e empacote depois (o pacote nao entra no commit); no zip, informe output e inputs e exclua bin/obj. " +
        "Git em pasta nova: `git init` antes do primeiro `git add/commit`, e configure `git -c user.email=... -c user.name=...` se o ambiente nao tiver identidade. " +
        "Tarefa de N passos: so responda texto depois de concluir todos. " +
        "Erro de tool, build ou teste: leia a mensagem, varie a abordagem (max 2 tentativas) e ajuste o trecho exato antes de repetir; nunca repita a mesma chamada nem o mesmo caminho. " +
        "NUNCA invente comando, tool ou caminho: confirme que existe (command -v, ls) antes de usar. " +
        "Tool bloqueada para o role: explique que a acao cabe a outro role, nao contorne por shell. " +
        "Qualidade: leia AGENTS.md/docs e o git log antes de planejar; grave todos os artefatos de uma vez, sem stub/TODO/placeholder; fail-fast honesto (tool/modelo inexistente = diga e pare, nunca invente); correcao por edit no trecho exato, nunca reescrevendo o arquivo inteiro; declare pronto so apos executar o ACEITE e anexar a evidencia (build/teste vermelho = tarefa nao concluida); responda no idioma do usuario.";

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
