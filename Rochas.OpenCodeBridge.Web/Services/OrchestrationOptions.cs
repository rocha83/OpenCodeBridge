namespace Rochas.OpenCodeBridge.Web.Services;

// Opções da orquestração híbrida (appsettings Orchestration).
public sealed class OrchestrationOptions
{
    // Quantas vezes um enunciado pode voltar ao orch para refinamento após falha.
    public int MaxTaskRetries { get; set; } = 2;
    // Executores simultâneos (0 = núcleos da CPU; GPU paraleliza, CPU llama serializa).
    public int MaxParallel { get; set; } = 0;
}
