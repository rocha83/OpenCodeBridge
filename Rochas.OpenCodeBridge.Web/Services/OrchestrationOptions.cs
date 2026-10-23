namespace Rochas.OpenCodeBridge.Web.Services;

// Opções da orquestração híbrida (appsettings Orchestration).
public sealed class OrchestrationOptions
{
    // Quantas vezes um enunciado pode voltar ao orch para refinamento após falha.
    public int MaxTaskRetries { get; set; } = 2;
    // Teto de turnos do loop agêntico (flag; sem corte de prosa, o teto limita).
    // <=0 desliga o teto (era do ctx 32k do 8B; hoje 262k). Freios vivos: anti-giro/repeat.
    public int MaxTurns { get; set; } = 0;
    // Executores simultâneos (0 = núcleos da CPU; GPU paraleliza, CPU llama serializa).
    public int MaxParallel { get; set; } = 0;
    // tok/s assumido quando o executor nunca foi sondado e roda em CPU
    // (llama E4B). Medido em 14/10: CPU 2.5 t/s, GPU 30 t/s.
    public double CpuDefaultTps { get; set; } = 2.5;
    // Revisao em lote do orch sobre os resultados (pos-execucao, via /Chat/Review).
    // Inativa por padrao: o fluxo segue decompose -> aprova -> executa -> sintetiza.
    public bool EnableReview { get; set; } = false;
    // Aprovar direto: sem editor na UI (decompose ja dispara a execucao).
    public bool DirectApprove { get; set; } = true;
    // Thinking do orch (decompose/sintese/revisao) visivel na UI em <details>.
    // Ligado por padrao: desligar economiza contexto persistido.
    public bool ShowThinking { get; set; } = true;
    // Temperatura do julgamento (review/reviewplan): baixa para obedecer ao
    // formato JSON sem divagar. Decompose/planejamento segue em 0.3 (média).
    public double ReviewTemperature { get; set; } = 0.1;
    // Temperatura da decomposição (análise + expansão em subtarefas): escala atual
    // baixa 0.1 / média 0.3 / alta 0.5; 0.3 preserva granularidade sem divagar.
    public double DecomposeTemperature { get; set; } = 0.3;
    // Thinking na decomposição ("events" = raciocina; "off" = direto, sem <think>).
    // Off corta o mimetismo na fonte via chat_template_kwargs; se o parser do serve
    // desviar a resposta p/ o canal reasoning, a pipeline promove a conteúdo.
    public string DecomposeThinking { get; set; } = "events";
    // Caminho bypass da UI (turno único em {bridge}/v1/responses, SEM ToolExecutor;
    // sanitize leve na bridge com passthrough). Com ele LIGADO o chat é só
    // conversa (sem tools/subagente no loop); a delegação Orch→executor segue
    // pelos endpoints Decompose/Orchestrate, que não usam este flag.
    public bool UseResponsesBypass { get; set; } = true;
}
