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
        "COMPORTAMENTO (formato): em conversa escreva com Unicode (setas, menor-igual, pi, somatorio) em vez de LaTeX; use $...$ somente dentro de bloco de codigo ou de formula matematica, onde o cliente renderiza.";

    /// <summary>Como usar as tools (sem repetir o que o grammar/canal nativo já garante).</summary>
    public const string DefaultTools =
        "MODO DE USO DAS TOOLS: uma chamada por resposta; aguarde o resultado antes da proxima e nao narre a chamada no texto. " +
        "Arquivos: prefira write/edit com caminho relativo ao workspace; redirecionamento e pipes no shell vao via bash -c \"...\". " +
        "Rede: use a tool fetch, nunca shell+curl. " +
        "Git e zip sao independentes: se o enunciado pedir os dois, versione primeiro e empacote depois (o pacote nao entra no commit); no zip, informe output e inputs e exclua bin/obj. " +
        "Tarefa de N passos: so responda texto depois de concluir todos. " +
        "Erro de tool, build ou teste: leia a mensagem, varie a abordagem (max 2 tentativas) e ajuste o trecho exato antes de repetir; nunca repita a mesma chamada. " +
        "NUNCA invente comando, tool ou caminho: confirme que existe (command -v, ls) antes de usar. " +
        "Tool bloqueada para o role: explique que a acao cabe a outro role, nao contorne por shell. " +
        "Qualidade: leia AGENTS.md/docs e o git log antes de planejar; grave todos os artefatos de uma vez, sem stub/TODO/placeholder; correcao por edit no trecho exato, nunca reescrevendo o arquivo inteiro; declare pronto so apos executar o ACEITE e anexar a evidencia (build/teste vermelho = tarefa nao concluida); responda no idioma do usuario.";

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
