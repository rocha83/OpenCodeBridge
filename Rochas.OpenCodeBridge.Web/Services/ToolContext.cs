namespace Rochas.OpenCodeBridge.Web.Services;

// Contexto de execução das tools (raiz do workspace + timeout).
public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 120;
}
