using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Web.Data;

public static class HarnessSeeder
{
    public static async Task SeedAsync(IHarnessProfileService profiles, IHarnessAttachmentService attachments, IHarnessLinter linter)
    {
        var existing = await profiles.ListAsync();
        if (existing.Any(p => p.Name == "OpenCode Summarized")) return;

        var core = @"- Paralelismo: chame tools independentes no mesmo turno.
- Gravacao extensiva: crie todos os artefatos de uma vez; sem stub/TODO.
- Correcao: use edit/sed; nunca reescreva o arquivo inteiro.
- ACEITE real: execute a verificacao e anexe evidencia.
- Fail-fast honesto: nao invente tool/modelo/comando.
- Contexto primeiro: leia docs e historico antes de planejar.
- Idioma do usuario; tool calls completas; diffs ao editar.
- Build de integridade: nao conclua com build/teste vermelho.
- Subagente: use a ferramenta de subagente do mesmo tipo e mesmo nome de modelo quando estiver trabalhando dentro de contextos iguais ou menores que 64k.";

        var linterFindings = linter.FindHardcodes(core);
        if (linterFindings.Count > 0) return;

        var id = await profiles.CreateAsync("OpenCode Summarized", "Resumo do harness do OpenCode sem hardcodes");
        var vid = await profiles.AddVersionAsync(id, "BridgeBypass", "opencode-summary-v1");
        await attachments.CreateAsync(vid, "Core", "general", core);
        var orchTail = @"- Delegacao: em tarefa grande, divida em sub-tarefas autocontidas.
- Agregue os resultados e re-execute o ACEITE de cada um.";
        await attachments.CreateAsync(vid, "RoleTail", "orch", orchTail);
        await profiles.ApproveVersionAsync(vid, "system");
    }
}
