namespace Rochas.OpenCodeBridge.Web.Services;

// Opções da orquestração híbrida (appsettings Orchestration).
public sealed class OrchestrationOptions
{
    // Quantas vezes um enunciado pode voltar ao orch para refinamento após falha.
    public int MaxTaskRetries { get; set; } = 2;
    // Executores simultâneos (GPU paraleliza; CPU llama serializa: use 1-2).
    public int MaxParallel { get; set; } = 3;
}
