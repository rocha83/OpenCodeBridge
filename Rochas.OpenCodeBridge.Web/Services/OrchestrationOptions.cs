namespace Rochas.OpenCodeBridge.Web.Services;

// Opções da orquestração híbrida (appsettings Orchestration).
public sealed class OrchestrationOptions
{
    // Quantas vezes um enunciado pode voltar ao orch para refinamento após falha.
    public int MaxTaskRetries { get; set; } = 2;
    // Executores simultâneos (0 = núcleos da CPU; GPU paraleliza, CPU llama serializa).
    public int MaxParallel { get; set; } = 0;
    // tok/s assumido quando o executor nunca foi sondado e roda em CPU
    // (llama). Na CPU a ETA correta depende disto: GPU chuta 7, CPU 2.5.
    public double CpuDefaultTps { get; set; } = 2.5;
    // Revisao em lote do 8B sobre os resultados (pos-execucao, via /Chat/Review).
    // Inativa por padrao: o fluxo segue decompose -> aprova -> executa -> sintetiza.
    public bool EnableReview { get; set; } = false;
    // Aprovar direto: sem editor na UI (decompose ja dispara a execucao).
    public bool DirectApprove { get; set; } = false;
}
