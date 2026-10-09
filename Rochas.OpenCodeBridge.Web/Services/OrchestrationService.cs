using System.Text.Json.Nodes;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Orquestração híbrida: orquestrador decompõe, executores rodam em paralelo,
// orquestrador sintetiza. Cada fase é mensagem persistida (explícita na conversa).
public sealed class OrchestrationService(
    IBridgeClient bridge,
    ISessionService sessions,
    IGenericRepository<Agent> agents,
    IToolExecutor tools) : IOrchestrationService
{
    private const int MaxTasks = 5;
    private const int MaxParallel = 2;
    private const int MaxToolTurns = 5;

    // Orch: divide o pedido em subtarefas técnicas. JSON estrito.
    // Trilhas paralelas: backend x frontend, com contratos explícitos.
    private const string DecomposeSystem =
        "Você é o orquestrador. Decomponha o pedido do usuário em 2 a 5 subtarefas " +
        "ATÔMICAS (1 ação verificável cada: 'crie a classe X com propósito Y', 'compile o " +
        "projeto', 'leia o arquivo Z'), independentes e autocontidas para agentes executores " +
        "trabalhando EM PARALELO, inclusive offline (cada prompt carrega todo o contexto: " +
        "caminhos, comandos, saída esperada com exemplo e critério de aceite). " +
        "Organize em trilhas: BACKEND (C#/.NET, DDD) e FRONTEND (web); se houver dependência " +
        "(ex.: contratos, DTOs, entidades), emita-a como subtarefa própria e referencie-a nas " +
        "dependentes. Cada prompt deve trazer interfaces e contratos explícitos para fluir sem " +
        "espera entre trilhas. Responda SOMENTE com JSON, sem markdown nem texto extra: " +
        "{\"tasks\":[{\"title\":\"verbo de ação curto\",\"prompt\":\"instrução completa para o executor\"}]}.";

    // Executor: enunciado rígido (1 tarefa, artefato + evidência, sem conversa).
    private const string ExecutorSystem =
        "Você é o executor. Execute exatamente a tarefa recebida, de forma direta e técnica. " +
        "Responda com o artefato pedido seguido de evidência curta do que foi feito. " +
        "Sem conversa, sem perguntas de volta.";

    // Orch: síntese final em pt-BR a partir dos resultados.
    private const string SynthesisSystem =
        "Você é o orquestrador. Sintetize os resultados dos executores abaixo em resposta " +
        "final direta ao usuário, em pt-BR. Se algum executor falhou, diga o que faltou.";

    public async Task<OrchestrateResult> OrchestrateAsync(int sessionId, int userId, string text, CancellationToken ct)
    {
        var session = await sessions.GetAsync(sessionId, userId);
        if (session is null) return new OrchestrateResult(false, "", "Sessão não encontrada", 0);
        if (!session.ExecutorAgentId.HasValue)
            return new OrchestrateResult(false, "", "Sessão sem executor selecionado", 0);

        await sessions.AddMessageAsync(sessionId, "user", text, "", null, null);
        await sessions.AddMessageAsync(sessionId, "assistant",
            "[Orquestrador] Analisando o pedido e dividindo em tarefas...", "", null, null);

        var agents = await ResolveAgentsAsync(session);
        if (agents is null) return new OrchestrateResult(false, "", "Orquestrador ou executor inválido", 0);

        var tasks = await DecomposeAsync(agents.Orch, text, ct);
        await sessions.AddMessageAsync(sessionId, "assistant", DivisionText(tasks), "", null, null);
        return await RunTasksAsync(sessionId, session, agents.Orch, agents.Exec, text, tasks, ct);
    }

    // Fase 1: valida, persiste pedido + preview, devolve tarefas (sem executar).
    public async Task<DecomposeResult> PreviewAsync(int sessionId, int userId, string text, CancellationToken ct)
    {
        var session = await sessions.GetAsync(sessionId, userId);
        if (session is null) return new DecomposeResult(false, new List<SubTask>(), "Sessão não encontrada");
        if (!session.ExecutorAgentId.HasValue)
            return new DecomposeResult(false, new List<SubTask>(), "Sessão sem executor selecionado");

        var agents = await ResolveAgentsAsync(session);
        if (agents is null) return new DecomposeResult(false, new List<SubTask>(), "Orquestrador ou executor inválido");

        await sessions.AddMessageAsync(sessionId, "user", text, "", null, null);
        var tasks = await DecomposeAsync(agents.Orch, text, ct);
        await sessions.AddMessageAsync(sessionId, "assistant", DivisionText(tasks), "", null, null);
        await sessions.TouchAsync(sessionId);
        return new DecomposeResult(true, tasks, "");
    }

    // Fase 2: executa tarefas aprovadas + sintetiza (sem redecompor).
    public async Task<OrchestrateResult> RunApprovedAsync(int sessionId, int userId, List<SubTask> tasks, CancellationToken ct)
    {
        var session = await sessions.GetAsync(sessionId, userId);
        if (session is null) return new OrchestrateResult(false, "", "Sessão não encontrada", 0);
        if (!session.ExecutorAgentId.HasValue)
            return new OrchestrateResult(false, "", "Sessão sem executor selecionado", 0);
        if (tasks.Count == 0) return new OrchestrateResult(false, "", "Nenhuma tarefa aprovada", 0);

        var agents = await ResolveAgentsAsync(session);
        if (agents is null) return new OrchestrateResult(false, "", "Orquestrador ou executor inválido", 0);

        await sessions.AddMessageAsync(sessionId, "assistant",
            $"[Orquestrador] Executando {tasks.Count} tarefa(s) aprovada(s)...", "", null, null);
        var lastUser = (await sessions.GetMessagesAsync(sessionId, 50)).LastOrDefault(m => m.Role == "user");
        return await RunTasksAsync(sessionId, session, agents.Orch, agents.Exec,
            lastUser?.Content ?? "", tasks, ct);
    }

    private async Task<AgentPair?> ResolveAgentsAsync(Session session)
    {
        var all = await agents.Query(new Agent());
        var orch = all.FirstOrDefault(a => a.Id == session.AgentId);
        var exec = session.ExecutorAgentId.HasValue
            ? all.FirstOrDefault(a => a.Id == session.ExecutorAgentId.Value)
            : null;
        if (orch is null || exec is null) return null;
        return new AgentPair(orch, exec);
    }

    private sealed record AgentPair(Agent Orch, Agent Exec);

    // Modo plan: temperatura 0.4 nos modelos (leitura/navegação, sem pressa criativa).
    private static double PlanTemp(Agent a) => a.Mode == "plan" ? 0.4 : a.Temperature;

    private static string DivisionText(List<SubTask> tasks) =>
        "[Orquestrador] Dividi em " + tasks.Count + " tarefa(s):\n" +
        string.Join("\n", tasks.Select((t, i) => $"{i + 1}. {t.Title}"));

    private async Task<OrchestrateResult> RunTasksAsync(int sessionId, Session session,
        Agent orch, Agent exec, string text, List<SubTask> tasks, CancellationToken ct)
    {

        var results = new string[tasks.Count];
        using var gate = new SemaphoreSlim(MaxParallel);
        await Task.WhenAll(tasks.Select(async (task, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await sessions.AddMessageAsync(sessionId, "assistant",
                    $"[Executor {i + 1}] Iniciado: {task.Title}", "", null, null);
                results[i] = await RunExecutorTaskAsync(sessionId, i, task, exec, ct);
                await sessions.AddMessageAsync(sessionId, "assistant",
                    $"[Executor {i + 1}] Concluído: {task.Title}\n{Truncate(results[i], 2000)}", "", null, null);
            }
            finally
            {
                gate.Release();
            }
        }));

        var dump = string.Join("\n\n", tasks.Select((t, i) => $"## Tarefa {i + 1}: {t.Title}\n{results[i]}"));
        var synth = await BridgeHelper.ChatAsync(bridge, orch.BridgeUrl, orch.Model,
            PlanTemp(orch), SynthesisSystem,
            new JsonArray { new JsonObject { ["role"] = "user", ["content"] = $"Pedido: {text}\n\nResultados:\n{dump}" } }, ct);
        if (!synth.Ok) return new OrchestrateResult(false, "", $"Síntese falhou: {synth.Error}", tasks.Count);

        await sessions.AddMessageAsync(sessionId, "assistant", synth.Content,
            synth.Thinking, synth.PromptTokens, synth.CompletionTokens);
        await sessions.TouchAsync(sessionId);
        return new OrchestrateResult(true, synth.Content, "", tasks.Count);
    }

    private async Task<List<SubTask>> DecomposeAsync(Agent orch, string text, CancellationToken ct)
    {
        var r = await BridgeHelper.ChatAsync(bridge, orch.BridgeUrl, orch.Model, PlanTemp(orch),
            DecomposeSystem,
            new JsonArray { new JsonObject { ["role"] = "user", ["content"] = text } }, ct);
        var tasks = ParseTasks(r.Ok ? r.Content : "");
        if (tasks.Count > 0) return tasks;
        // Fallback honesto: sem decomposição válida, 1 tarefa com o pedido integral.
        return new List<SubTask> { new("Pedido integral", text) };
    }

    public static List<SubTask> ParseTasks(string content)
    {
        try
        {
            int start = content.IndexOf('{');
            int end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return new List<SubTask>();
            var root = JsonNode.Parse(content[start..(end + 1)])?.AsObject();
            var arr = root?["tasks"]?.AsArray();
            if (arr is null) return new List<SubTask>();
            return arr
                .Select(n => n?.AsObject())
                .Where(o => o is not null && !string.IsNullOrWhiteSpace(o["prompt"]?.GetValue<string>()))
                .Select(o => new SubTask(
                    o!["title"]?.GetValue<string>() is string t && !string.IsNullOrWhiteSpace(t) ? t.Trim() : "Tarefa",
                    o!["prompt"]!.GetValue<string>()))
                .Take(MaxTasks)
                .ToList();
        }
        catch
        {
            return new List<SubTask>();
        }
    }

    // Executor com tools no modo build (loop como o Stream); no modo plan, texto puro.
    private async Task<string> RunExecutorTaskAsync(int sessionId, int index, SubTask task, Agent exec, CancellationToken ct)
    {
        bool build = (exec.Mode ?? "build") != "plan";
        var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = task.Prompt } };
        var collected = new System.Text.StringBuilder();
        for (int turn = 0; turn < (build ? MaxToolTurns : 1); turn++)
        {
            var t = await BridgeHelper.ChatTurnAsync(bridge, exec.BridgeUrl, exec.Model,
                PlanTemp(exec), ExecutorSystem, history,
                build ? ToolDefinitions.GetTools(exec.Mode) : null, ct);
            if (!t.Ok) return $"[Executor {index + 1}] FALHOU: {t.Error}";
            collected.Append(t.Content);
            if (t.Calls.Count == 0) break;
            var assistantMsg = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = t.Content,
                ["tool_calls"] = new JsonArray(),
            };
            var tcArray = (JsonArray)assistantMsg["tool_calls"]!;
            foreach (var call in t.Calls)
                tcArray.Add(new JsonObject
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Args },
                });
            history.Add(assistantMsg);
            foreach (var call in t.Calls)
            {
                var result = tools.Execute(call.Name, call.Args, 120, exec.Mode);
                await sessions.AddMessageAsync(sessionId, "assistant",
                    $"[Executor {index + 1}] Executou {call.Name}: " +
                    (result.Success ? "ok" : $"falhou ({result.Error})"), "", null, null);
                string content = result.Success ? result.Output : $"Error: {result.Error}";
                if (content.Length > 2000) content = content[..2000] + "\n[truncado]";
                history.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call.Id,
                    ["content"] = content,
                });
            }
        }
        return collected.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n[truncado]";

    public sealed record SubTask(string Title, string Prompt);
}
