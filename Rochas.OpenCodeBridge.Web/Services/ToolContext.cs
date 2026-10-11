namespace Rochas.OpenCodeBridge.Web.Services;

// Contexto de execução das tools (raiz do workspace + timeout + agente chamador).
public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
    public string AgentName { get; init; } = "";
    public string BridgeUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public double Temperature { get; init; } = 0.2;
    // Sink de progresso (só o subagente usa): (tool, ok null=start, resumo).
    public Func<string, bool?, string, Task>? ProgressAsync { get; set; }
}
