using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core.Tools;

// Contexto UNICO de execucao das tools (bridge + Web). O superset fica aqui:
// o host da bridge usa o minimo (workspace/upstream/timeout/log) e a Web
// acrescenta o agente dono e os sinks de progresso. Nao existe copia no Web.

/// <summary>Progresso de tool (Ok null = inicio). Sub = id do subagente.</summary>
public sealed record ToolProgress(string Name, string? Sub, bool? Ok, string Summary);

/// <summary>Trilha de tool interna do sub (o handler nao conhece a sessao).</summary>
public sealed record InnerToolLog(string Agent, string Name, string Args, bool Ok, string Output, long Ms, string? Sub);

/// <summary>Contexto de execucao de uma tool.</summary>
public sealed class ToolContext
{
    // Raizes extras permitidas FORA do workspace (politica do HOST): a Web fica
    // no padrao (/mnt, /media) e a bridge acrescenta /tmp, onde vivem os cenarios.
    public static readonly string[] DefaultExtraRoots = { "/mnt", "/media" };
    public string[] ExtraRoots { get; init; } = DefaultExtraRoots;
    public string WorkspaceRoot { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 300;
    public string UpstreamUrl { get; init; } = "http://127.0.0.1:4100";
    public string ApiKey { get; init; } = "";
    // Role do agente que pediu a tool ("plan", "build", "exec"...). Decide o que a
    // tool pode fazer: plan e somente leitura; build/exec escrevem.
    public string Role { get; init; } = "";
    // Agente dono do contexto (caminho Web); vazio no caminho bridge.
    public string AgentName { get; init; } = "";
    public string BridgeUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public double Temperature { get; init; } = 0.1;
    // System do agente dono do contexto (o sub o usa como system; vazio = pinado).
    public string SystemPrompt { get; init; } = "";
    // Sinks do caminho Web (o bridge nao os usa).
    public Func<ToolProgress, Task>? ProgressAsync { get; set; }
    public Func<string, string, string, Task>? SubChunkAsync { get; set; }
    public Func<InnerToolLog, Task>? LogInnerAsync { get; set; }
    // Sink de log injetado pelo host (a bridge passa o console/log JSON).
    public Action<JsonObject>? Log { get; init; }
}
