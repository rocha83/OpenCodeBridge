namespace Rochas.OpenCodeBridge.Tools;

// Contexto de execução das tools na bridge (raiz do workspace + timeout).
public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
}
