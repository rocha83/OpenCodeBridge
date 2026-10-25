namespace Rochas.OpenCodeBridge.Core.Tools;

// Contexto de execução das tools na bridge (raiz do workspace + timeout + upstream).
public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
    public string UpstreamUrl { get; init; } = "http://127.0.0.1:4100";
    public string ApiKey { get; init; } = "";
    // Role do agente que pediu a tool ("plan", "build", "exec"...). Decide o que a
    // tool pode fazer: plan é somente leitura; build/exec escrevem.
    public string Role { get; init; } = "";
    // Sink de log injetado pelo host (o nucleo nao conhece o console).
    public Action<System.Text.Json.Nodes.JsonObject>? Log { get; init; }
}
