using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Porta única de execução de tools (serviço de domínio; só despacha).
public interface IToolExecutor
{
    ToolResult Execute(string name, string arguments, int timeoutSeconds = 120, string? mode = null, Agent? agent = null);
    // Sink de progresso (só o subagente usa via ChatController).
    Func<string, bool?, string, Task>? ProgressAsync { get; set; }
}
