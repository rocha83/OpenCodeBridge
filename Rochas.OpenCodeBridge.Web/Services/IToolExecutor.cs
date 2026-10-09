namespace Rochas.OpenCodeBridge.Web.Services;

// Porta única de execução de tools (serviço de domínio; só despacha).
public interface IToolExecutor
{
    ToolResult Execute(string name, string arguments, int timeoutSeconds = 120);
}
