// Testes unitários da orquestração híbrida (bridge fake, sem rede/GPU).
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130

using System.IO;
using System.Text.Json.Nodes;
using Rochas.Data.Specification.Enums;
using Rochas.DapperRepository;
using Rochas.OpenCodeBridge.Web.Data;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class OrchestrationTests
{
    private static int Failures;

    // Bridge fake: decomposição fixa em 2 tarefas; executor ecoa; síntese fixa.
    private sealed class FakeBridge : IBridgeClient
    {
        public List<string> SystemsSeen = new();
        private int _toolCallsServed;
        public Task<(bool ok, string error)> StreamAsync(string bridgeUrl, string model, double temperature,
            string systemPrompt, JsonArray messages, Stream output, CancellationToken ct,
            bool includeTools = true, JsonArray? tools = null)
        {
            SystemsSeen.Add(systemPrompt);
            string lastUser = messages.OfType<System.Text.Json.Nodes.JsonObject>()
                .LastOrDefault(m => m["role"]?.GetValue<string>() == "user")?["content"]?.GetValue<string>() ?? "";
            string sse;
            if (lastUser.Contains("[usetools]") && _toolCallsServed == 0)
            {
                _toolCallsServed++;
                sse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"shell\",\"arguments\":\"{\\\"command\\\": \\\"echo via-executor\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
            }
            else
            {
                string content = systemPrompt.Contains("Decomponha")
                    ? "{\"tasks\":[{\"title\":\"Tarefa A\",\"prompt\":\"faça A\"},{\"title\":\"Tarefa B\",\"prompt\":\"[usetools] faça B\"}]}"
                    : systemPrompt.Contains("Sintetize")
                        ? "sintese-final"
                        : "artefato-executado";
                sse = $"data: {{\"choices\":[{{\"delta\":{{\"content\":{JsonEncode(content)}}},\"finish_reason\":\"stop\"}}]}}\n\ndata: [DONE]\n\n";
            }
            var bytes = System.Text.Encoding.UTF8.GetBytes(sse);
            output.Write(bytes, 0, bytes.Length);
            return Task.FromResult((true, ""));
        }

        private static string JsonEncode(string s) =>
            System.Text.Json.JsonSerializer.Serialize(s);
    }

    internal static int Run()
    {
        ParseValid();
        ParseFallback();
        PlanToolsFilter();
        PipelineHybrid().GetAwaiter().GetResult();
        PipelineNoExecutor().GetAwaiter().GetResult();
        TwoPhases().GetAwaiter().GetResult();
        ExecutorTools().GetAwaiter().GetResult();

        System.Console.WriteLine($"=== Orchestration Unit: {8 - Failures}/8 PASS, {Failures} FAIL ===");
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static void ParseValid()
    {
        var tasks = OrchestrationService.ParseTasks("texto antes {\"tasks\":[{\"title\":\"A\",\"prompt\":\"p1\"},{\"title\":\"B\",\"prompt\":\"p2\"}]} depois");
        Check(tasks.Count == 2 && tasks[0].Title == "A" && tasks[1].Prompt == "p2", "U-orch-parse");
    }

    private static void ParseFallback()
    {
        var tasks = OrchestrationService.ParseTasks("sem json nenhum");
        Check(tasks.Count == 0, "U-orch-parse-fallback");
    }

    private static void PlanToolsFilter()
    {        var plan = ToolDefinitions.GetTools("plan");
        var names = plan.Select(t => (t as System.Text.Json.Nodes.JsonObject)?["function"]?["name"]?.GetValue<string>()).ToList();
        Check(plan.Count == 3 && !names.Contains("shell") && !names.Contains("write") && !names.Contains("edit")
            && names.Contains("read") && names.Contains("grep") && names.Contains("glob"), "U-orch-plan-tools");
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"plan-{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var exec = new ToolExecutor(root, System.IO.Path.Combine(root, "audit.log"));
            var denied = exec.Execute("shell", "{\"command\": \"echo x\"}", 30, "plan");
            var allowed = exec.Execute("glob", "{\"pattern\": \"*.cs\"}", 30, "plan");
            Check(!denied.Success && denied.Error.Contains("modo plan") && allowed.Success, "U-orch-plan-backstop");
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { }
        }
    }

    private static (OrchestrationService svc, FakeBridge fake, string db) Setup()
    {
        string db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"orch-{System.Guid.NewGuid():N}.db");
        AppDb.Init(db);
        var sessions = new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var messages = new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var fake = new FakeBridge();
        var svc = new OrchestrationService(fake, new SessionService(sessions, sessions, messages, messages),
            new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString),
            new ToolExecutor(System.IO.Path.GetTempPath(), System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orch-tools.log")));
        return (svc, fake, db);
    }

    private static async System.Threading.Tasks.Task PipelineHybrid()
    {
        var (svc, fake, db) = Setup();
        try
        {
            var agents = new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await agents.Add(new Agent { Name = "orch", Model = "m1" });
            await agents.Add(new Agent { Name = "exec", Model = "m2" });
            var orch = (await agents.Query(new Agent())).First(a => a.Name == "orch");
            var exec = (await agents.Query(new Agent())).First(a => a.Name == "exec");
            var users = new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await users.Add(new User { Name = "u", Email = "u@u.com", PasswordHash = "h" });
            var user = (await users.Query(new User())).First(u => u.Email == "u@u.com");

            var sessions = new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            var s = await sessions.Add(new Session { UserId = user.Id ?? 0, AgentId = orch.Id ?? 0, ExecutorAgentId = exec.Id, Title = "t" });
            var created = (await sessions.Query(new Session { UserId = user.Id ?? 0 })).OrderByDescending(x => x.Id ?? 0).First();

var r = await svc.OrchestrateAsync(created.Id ?? 0, user.Id ?? 0, "construa algo", CancellationToken.None);
            var msgs = await new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString)
                .Query(new SessionMessage { SessionId = created.Id });
            bool hasDecomp = msgs.Any(m => m.Content.Contains("[Orquestrador] Dividi em 2"));
            bool hasExec = msgs.Any(m => m.Content.Contains("[Executor 1]")) && msgs.Any(m => m.Content.Contains("[Executor 2]"));
            bool hasFinal = msgs.Any(m => m.Content == "sintese-final");
            Check(r.Ok && r.TaskCount == 2 && hasDecomp && hasExec && hasFinal, "U-orch-pipeline");
        }
        finally
        {
            try { System.IO.File.Delete(db); } catch { }
        }
    }

    private static async System.Threading.Tasks.Task PipelineNoExecutor()
    {
        var (svc, fake, db) = Setup();
        try
        {
            var agents = new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await agents.Add(new Agent { Name = "solo", Model = "m1" });
            var solo = (await agents.Query(new Agent())).First(a => a.Name == "solo");
            var users = new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await users.Add(new User { Name = "u2", Email = "u2@u.com", PasswordHash = "h" });
            var user = (await users.Query(new User())).First(u => u.Email == "u2@u.com");
            var sessions = new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await sessions.Add(new Session { UserId = user.Id ?? 0, AgentId = solo.Id ?? 0, Title = "t" });
            var created = (await sessions.Query(new Session { UserId = user.Id ?? 0 })).OrderByDescending(x => x.Id ?? 0).First();
            var r = await svc.OrchestrateAsync(created.Id ?? 0, user.Id ?? 0, "ola", CancellationToken.None);
            Check(!r.Ok && r.Error.Contains("executor"), "U-orch-no-executor");
        }
        finally
        {
            try { System.IO.File.Delete(db); } catch { }
        }
    }

    private static async System.Threading.Tasks.Task TwoPhases()
    {        var (svc, fake, db) = Setup();
        try
        {
            var agents = new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await agents.Add(new Agent { Name = "o3", Model = "m1" });
            await agents.Add(new Agent { Name = "e3", Model = "m2" });
            var orch = (await agents.Query(new Agent())).First(a => a.Name == "o3");
            var exec = (await agents.Query(new Agent())).First(a => a.Name == "e3");
            var users = new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await users.Add(new User { Name = "u3", Email = "u3@u.com", PasswordHash = "h" });
            var user = (await users.Query(new User())).First(u => u.Email == "u3@u.com");
            var sessions = new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await sessions.Add(new Session { UserId = user.Id ?? 0, AgentId = orch.Id ?? 0, ExecutorAgentId = exec.Id, Title = "t" });
            var created = (await sessions.Query(new Session { UserId = user.Id ?? 0 })).OrderByDescending(x => x.Id ?? 0).First();

            var preview = await svc.PreviewAsync(created.Id ?? 0, user.Id ?? 0, "construa algo", CancellationToken.None);
            var run = await svc.RunApprovedAsync(created.Id ?? 0, user.Id ?? 0,
                preview.Tasks.Select(t => new OrchestrationService.SubTask(t.Title, t.Prompt)).ToList(),
                CancellationToken.None);
            var msgs = await new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString)
                .Query(new SessionMessage { SessionId = created.Id });
            bool approved = msgs.Any(m => m.Content.Contains("aprovada"));
            bool final = msgs.Any(m => m.Content == "sintese-final");
            Check(preview.Ok && preview.Tasks.Count == 2 && run.Ok && approved && final, "U-orch-two-phases");
        }
        finally
        {
            try { System.IO.File.Delete(db); } catch { }
        }
    }

    private static async System.Threading.Tasks.Task ExecutorTools()
    {
        // Tarefa B carrega "[usetools]": o fake devolve 1 tool_call shell e o
        // executor deve executar de verdade (echo) e seguir para a síntese.
        var (svc, fake, db) = Setup();
        try
        {
            var agents = new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await agents.Add(new Agent { Name = "o4", Model = "m1" });
            await agents.Add(new Agent { Name = "e4", Model = "m2" });
            var orch = (await agents.Query(new Agent())).First(a => a.Name == "o4");
            var exec = (await agents.Query(new Agent())).First(a => a.Name == "e4");
            var users = new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await users.Add(new User { Name = "u4", Email = "u4@u.com", PasswordHash = "h" });
            var user = (await users.Query(new User())).First(u => u.Email == "u4@u.com");
            var sessions = new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await sessions.Add(new Session { UserId = user.Id ?? 0, AgentId = orch.Id ?? 0, ExecutorAgentId = exec.Id, Title = "t" });
            var created = (await sessions.Query(new Session { UserId = user.Id ?? 0 })).OrderByDescending(x => x.Id ?? 0).First();

            var r = await svc.OrchestrateAsync(created.Id ?? 0, user.Id ?? 0, "construa algo", CancellationToken.None);
            var msgs = await new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString)
                .Query(new SessionMessage { SessionId = created.Id });
            bool toolRan = msgs.Any(m => m.Content.Contains("[Executor 2] Executou shell"));
            Check(r.Ok && toolRan, "U-orch-executor-tools");
        }
        finally
        {
            try { System.IO.File.Delete(db); } catch { }
        }
    }
}
