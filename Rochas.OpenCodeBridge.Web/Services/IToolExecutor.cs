using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Porta única de execução de tools (serviço de domínio; só despacha).
public interface IToolExecutor
{
    ToolResult Execute(string name, string arguments, int timeoutSeconds = 120, string? mode = null, Agent? agent = null);
    // Sink de progresso das tools INTERNAS do sub (write/shell/edit).
    // O agente principal não usa este sink: o ChatController emite o
    // progresso dele direto no SSE; aqui só trafega o do sub até o SSE.
    Func<ToolProgress, Task>? ProgressAsync { get; set; }
    // Chunks ao vivo do subagente (sub, kind thinking|content, texto).
    Func<string, string, string, Task>? SubChunkAsync { get; set; }
}
