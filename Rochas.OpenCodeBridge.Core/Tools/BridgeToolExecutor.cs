using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core.Tools;

// Despachante de tools na bridge: executa handlers leaf no workspace.
// Sem gates de agente/modo (a Web autoriza); shell segue com as travas
// internas do ShellToolHandler + ShellInspector.
public sealed class BridgeToolExecutor
{
    private readonly Dictionary<string, IToolHandler> _handlers;
    private readonly string _workspaceRoot;
    private readonly string _upstreamUrl;
    private readonly string _apiKey;
    private readonly string _role;
    private readonly Action<JsonObject>? _log;

    public BridgeToolExecutor(string workspaceRoot, string upstreamUrl = "http://127.0.0.1:4100", string apiKey = "", Action<JsonObject>? log = null, string role = "")
    {
        _workspaceRoot = workspaceRoot;
        _upstreamUrl = upstreamUrl;
        _apiKey = apiKey;
        _role = role;
        _log = log;
        _handlers = DefaultHandlers().ToDictionary(h => h.ToolName, StringComparer.Ordinal);
    }

    public static IEnumerable<IToolHandler> DefaultHandlers()
    {
        yield return new ShellToolHandler();
        yield return new ReadToolHandler();
        yield return new WriteToolHandler();
        yield return new EditToolHandler();
        yield return new GrepToolHandler();
        yield return new GlobToolHandler();
        yield return new FetchToolHandler();
        yield return new GitToolHandler();
        yield return new SubagentToolHandler();
    }

    public ToolResult Execute(string name, string arguments, int timeoutSeconds = 300)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult.Fail("tool sem nome");
        string key = name is "bash" or "sh" ? "shell" : name is "webfetch" ? "fetch" : name is "task" ? "subagent" : name;
        if (!_handlers.TryGetValue(key, out IToolHandler? handler))
            return ToolResult.Fail($"tool desconhecida: '{name}' (válidas: shell, read, write, edit, grep, glob, fetch, git)");
        var context = new ToolContext { WorkspaceRoot = _workspaceRoot, TimeoutSeconds = timeoutSeconds, UpstreamUrl = _upstreamUrl, ApiKey = _apiKey, Role = _role, Log = _log };
        try { return handler.Handle(arguments ?? "", context); }
        catch (Exception ex) { return ToolResult.Fail($"falha interna na tool '{name}': {ex.Message}"); }
    }

    public JsonObject ToolResultJson(ToolResult result) => new()
    {
        ["ok"] = result.Success,
        ["output"] = result.Success ? result.Output : "",
        ["error"] = result.Success ? "" : result.Error,
    };
}
