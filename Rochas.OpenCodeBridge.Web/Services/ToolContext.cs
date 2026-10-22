namespace Rochas.OpenCodeBridge.Web.Services;

// Contexto de execução das tools (raiz do workspace + timeout + agente chamador).
public sealed record ToolProgress(string Name, string? Sub, bool? Ok, string Summary);

// Trilha de tool interna do sub p/ persistência (o handler não conhece a sessão;
// o ChatController injeta o sink com ela em closure).
public sealed record InnerToolLog(string Agent, string Name, string Args, bool Ok, string Output, long Ms, string? Sub);

public sealed class ToolContext
{
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
    public string AgentName { get; init; } = "";
    public string BridgeUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public double Temperature { get; init; } = 0.1;
    // System do agente dono do contexto (o sub o usa como system; vazio = pinado).
    public string SystemPrompt { get; init; } = "";
    // Progresso de tool (ok null=start). Sub = id do subagente (só internas).
    public Func<ToolProgress, Task>? ProgressAsync { get; set; }
    // Chunks ao vivo do subagente: (sub, kind thinking|content, texto).
    public Func<string, string, string, Task>? SubChunkAsync { get; set; }
    // Persistência das tools internas do sub (só invocado quando há Sub).
    public Func<InnerToolLog, Task>? LogInnerAsync { get; set; }
}
