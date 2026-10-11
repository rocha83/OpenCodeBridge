namespace Rochas.OpenCodeBridge.Web.Services;

// Contexto de execução das tools (raiz do workspace + timeout + agente chamador).
public sealed record ToolProgress(string Name, string? Sub, bool? Ok, string Summary);

public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
    public string AgentName { get; init; } = "";
    public string BridgeUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public double Temperature { get; init; } = 0.2;
    // Progresso de tool (ok null=start). Sub = id do subagente (só internas).
    public Func<ToolProgress, Task>? ProgressAsync { get; set; }
    // Chunks ao vivo do subagente: (sub, kind thinking|content, texto).
    public Func<string, string, string, Task>? SubChunkAsync { get; set; }
}
