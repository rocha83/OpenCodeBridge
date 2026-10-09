namespace Rochas.OpenCodeBridge.Web.Services;

// Contrato do serviço de orquestração híbrida (orch decompõe, executores em paralelo).
public interface IOrchestrationService
{
    // Executa o pipeline completo e retorna a síntese final. Todo o processo
    // é persistido como mensagens da sessão (explícito na conversa).
    Task<OrchestrateResult> OrchestrateAsync(int sessionId, int userId, string text, CancellationToken ct);
    // Fase 1: só decompõe e persiste o preview (pipe completa só após aprovar).
    Task<DecomposeResult> PreviewAsync(int sessionId, int userId, string text, CancellationToken ct);
    // Fase 2: executa tarefas aprovadas (sem redecompor nem duplicar a msg do usuário).
    Task<OrchestrateResult> RunApprovedAsync(int sessionId, int userId, List<OrchestrationService.SubTask> tasks, CancellationToken ct);
}

public sealed record OrchestrateResult(bool Ok, string Synthesis, string Error, int TaskCount);
public sealed record DecomposeResult(bool Ok, List<OrchestrationService.SubTask> Tasks, string Error);
