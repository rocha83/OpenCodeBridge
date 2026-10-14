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
            yield return new FetchToolHandler();
        }

        // Sink de progresso: o ChatController liga só na chamada "subagent".
        // O agente principal não usa este sink (o loop emite o dele direto
        // no SSE); o sub repassa ao executor interno (write/shell/edit).
        // Sub = id do subagente dono (só nas internas); chunks ao vivo idem.
        public Func<ToolProgress, Task>? ProgressAsync { get; set; }
        public string? SubId { get; set; }
        public Func<string, string, string, Task>? SubChunkAsync { get; set; }
        public Func<InnerToolLog, Task>? LogInnerAsync { get; set; }

        // agent = contexto de execucao (modelo/bridge); allowAgent = dono da
        // permissao (p/ subagent: o orquestrador que delega, nao o executor
        // que roda a fase — o executor quase nunca tem subagent no CSV).
        public ToolResult Execute(string name, string arguments, int timeoutSeconds = 300, string? mode = null, Models.Agent? agent = null, Models.Agent? allowAgent = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                return ToolResult.Fail("tool sem nome");

            // Nomes alternativos do shell caem no handler "shell".
            string key = name is "bash" or "sh" ? "shell" : name;

            // Modo plan: leitura + navegação shell (retaguarda; o anúncio já filtra).
            if (string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase) && (key is "write" or "edit"))
                return ToolResult.Fail($"Não permitido em modo plan: tool '{key}' indisponível (sem escrita)");

            if (!_handlers.TryGetValue(key, out IToolHandler? handler))
                return ToolResult.Fail($"Não permitido: tool '{name}' fora do escopo permitido (válidas: shell, read, write, edit, grep, glob, fetch, git, subagent)");

            // Backstop do cadastro: anunciada ou não, tool fora do CSV do agente não executa.
            // (permissão do orquestrador quando ele delega; contexto segue no executor).
            var gate = allowAgent ?? agent;
            if (gate is not null && !ToolDefinitions.Allowed(gate, key))
                return ToolResult.Fail($"Não permitido: tool '{key}' fora do harness do agente '{gate.Name}'");
            // Preset display (sem shell): shell anunciada só p/ `echo` simples.
            if (agent is not null && key == "shell" && !ToolDefinitions.ToolSet(agent).Contains("shell")
                && !ToolDefinitions.DisplayOnly(ToolDefinitions.ToolSet(agent), arguments ?? ""))
                return ToolResult.Fail("Não permitido: preset exibição só aceita 'echo' simples (sem pipes/redirects)");

            var context = new ToolContext
            {
                WorkspaceRoot = _repoPath,
                TimeoutSeconds = timeoutSeconds,
                AgentName = agent?.Name ?? "",
                BridgeUrl = agent?.BridgeUrl ?? "",
                Model = agent?.Model ?? "",
                Temperature = agent?.Temperature ?? 0.4,
                ProgressAsync = ProgressAsync,
                SubChunkAsync = SubChunkAsync,
                LogInnerAsync = LogInnerAsync,
            };
            ToolResult result;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (ProgressAsync is not null)
                    try { ProgressAsync(new ToolProgress(key, SubId, null, "")).GetAwaiter().GetResult(); } catch { }
                result = _telemetry is not null
                    ? _telemetry.Timed($"tool.{key}", () => handler.Handle(arguments ?? "", context))
                    : handler.Handle(arguments ?? "", context);
                if (ProgressAsync is not null)
                    try { ProgressAsync(new ToolProgress(key, SubId, result.Success, result.Success ? result.Output : result.Error ?? "")).GetAwaiter().GetResult(); } catch { }
            }
            catch (Exception ex)
            {
                result = ToolResult.Fail($"falha interna na tool '{name}': {ex.Message}");
            }
            sw.Stop();
            // Trilha das internas do sub (as do agente o ChatController já grava
            // com o id da mensagem do turno; aqui o Sub identifica o card dono).
            if (SubId is not null && LogInnerAsync is not null)
            {
                try
                {
                    LogInnerAsync(new InnerToolLog(context.AgentName, key, arguments ?? "",
                        result.Success, result.Success ? result.Output : result.Error ?? "",
                        sw.ElapsedMilliseconds, SubId)).GetAwaiter().GetResult();
                }
                catch { /* trilha best-effort */ }
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
            s.Length <= max ? s : s[..max];
    }
}
