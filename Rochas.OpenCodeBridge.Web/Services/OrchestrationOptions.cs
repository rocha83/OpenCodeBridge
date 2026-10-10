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
    // Thinking do orch (decompose/sintese/revisao) visivel na UI em <details>.
    // Ligado por padrao: desligar economiza contexto persistido.
    public bool ShowThinking { get; set; } = true;
    // Temperatura do julgamento (review/reviewplan): baixa para obedecer ao
    // formato JSON sem divagar. Decompose/planejamento segue em 0.4 (criativo).
    public double ReviewTemperature { get; set; } = 0.1;
    // Temperatura da decomposição (análise + expansão em subtarefas): Qwen oficial
    // recomenda 0.6 p/ thinking mode (0.2 murchava a granularidade: 1-3 tarefas).
    public double DecomposeTemperature { get; set; } = 0.6;
    // Thinking na decomposição ("events" = raciocina; "off" = direto, sem <think>).
    // Off corta o mimetismo na fonte via chat_template_kwargs; se o parser do serve
    // desviar a resposta p/ o canal reasoning, a pipeline promove a conteúdo.
    public string DecomposeThinking { get; set; } = "events";
}
