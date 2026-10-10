namespace Rochas.OpenCodeBridge.Web.Services;

// Contexto de execução das tools (raiz do workspace + timeout + agente chamador).
public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 120;
    public string AgentName { get; init; } = "";
    public string BridgeUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public double Temperature { get; init; } = 0.2;
}
