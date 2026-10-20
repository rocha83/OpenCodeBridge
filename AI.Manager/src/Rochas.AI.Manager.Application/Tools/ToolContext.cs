namespace Rochas.AI.Manager.Application.Tools;

// Contexto de execução das tools na bridge (raiz do workspace + timeout + upstream).
public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
    public string UpstreamUrl { get; init; } = "http://127.0.0.1:4100";
    public string ApiKey { get; init; } = "";
}
