using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services
{
    // Porta única de execução de tools: só despacha para o handler da tool.
    public sealed class ToolExecutor : IToolExecutor
    {
        private readonly Dictionary<string, IToolHandler> _handlers;
        private readonly string _repoPath;
        private readonly string _logPath;
        private readonly IDiagnosticTelemetry? _telemetry;

        public ToolExecutor(IEnumerable<IToolHandler> handlers, string repoPath, string logPath = "/tmp/tool-executor.log", IDiagnosticTelemetry? telemetry = null)
        {
            _handlers = handlers.ToDictionary(h => h.ToolName, StringComparer.Ordinal);
            _repoPath = repoPath;
            _logPath = logPath;
            _telemetry = telemetry;
        }

        // Compat: construção manual (testes) com os handlers padrão.
        public ToolExecutor(string repoPath, string logPath = "/tmp/tool-executor.log", string verbosity = "normal")
            : this(DefaultHandlers(), repoPath, logPath)
        {
        }

        public static IEnumerable<IToolHandler> DefaultHandlers()
        {
            yield return new ShellToolHandler();
            yield return new ReadToolHandler();
            yield return new WriteToolHandler();
            yield return new EditToolHandler();
            yield return new GrepToolHandler();
            yield return new GlobToolHandler();
        }

        public ToolResult Execute(string name, string arguments, int timeoutSeconds = 120)
        {
            if (string.IsNullOrWhiteSpace(name))
                return ToolResult.Fail("tool sem nome");

            // Nomes alternativos do shell caem no handler "shell".
            string key = name is "bash" or "sh" ? "shell" : name;

            if (!_handlers.TryGetValue(key, out IToolHandler? handler))
                return ToolResult.Fail($"tool '{name}' não suportada");

            var context = new ToolContext { WorkspaceRoot = _repoPath, TimeoutSeconds = timeoutSeconds };
            ToolResult result;
            try
            {
                result = _telemetry is not null
                    ? _telemetry.Timed($"tool.{key}", () => handler.Handle(arguments ?? "", context))
                    : handler.Handle(arguments ?? "", context);
            }
            catch (Exception ex)
            {
                result = ToolResult.Fail($"falha interna na tool '{name}': {ex.Message}");
            }

            Audit(name, result);
            return result;
        }

        private void Audit(string name, ToolResult result)
        {
            try
            {
                int bytes = result.Success ? result.Output.Length : result.Error.Length;
                File.AppendAllText(_logPath, new JsonObject
                {
                    ["event"] = "exec",
                    ["tool"] = name,
                    ["ok"] = result.Success,
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["output_bytes"] = bytes,
                    ["output_head"] = Truncate(result.Success ? result.Output : result.Error, 1000),
                }.ToJsonString() + "\n");
            }
            catch { }
        }

        private static string Truncate(string s, int max) =>
            s.Length <= max ? s : s[..max] + "\n[truncado]";
    }
}
