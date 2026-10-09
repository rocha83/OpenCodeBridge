using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Orquestração híbrida: orquestrador decompõe, executores rodam em paralelo,
// orquestrador sintetiza. Cada fase é mensagem persistida (explícita na conversa).
public sealed class OrchestrationService(
    IBridgeClient bridge,
    ISessionService sessions,
    IGenericRepository<Agent> agents,
    IToolExecutor tools,
    IOptions<OrchestrationOptions> orchestrationOptions) : IOrchestrationService
{
    private const int MaxTasks = 16;
    private const int MaxToolTurns = 5;
    private const int MaxDecomposeTries = 3;
    private const int MinTasks = 2;
    private readonly int _maxTaskRetries = Math.Max(0, orchestrationOptions.Value.MaxTaskRetries);
    private readonly int _maxParallel = Math.Clamp(
        orchestrationOptions.Value.MaxParallel <= 0 ? Environment.ProcessorCount : orchestrationOptions.Value.MaxParallel,
        1, 16);

    // Orch: divide o pedido em subtarefas técnicas. JSON estrito.
    // Trilhas paralelas: backend x frontend, com contratos explícitos.
    private const string DecomposeSystem =
        "Você é o orquestrador. Decomponha o pedido do usuário em subtarefas " +
        "ATÔMICAS (1 ação verificável cada: 'crie a classe X com propósito Y', 'compile o " +
        "projeto', 'leia o arquivo Z'), independentes e autocontidas para agentes executores " +
        "trabalhando EM PARALELO, inclusive offline (cada prompt carrega todo o contexto: " +
        "caminhos, comandos, saída esperada com exemplo e critério de aceite). Escalone a " +
        "quantidade ao escopo: no mínimo 6; 8+ em escopos médios (CRUD + CI/CD); 12+ em " +
        "ecossistemas (portal + barramento + dados + ML). É PROIBIDO devolver 1 tarefa " +
        "ecoando o pedido: divida para conquistar o limite do executor. " +
        "Organize em trilhas: BACKEND (C#/.NET, DDD) e FRONTEND (web); se houver dependência " +
        "(ex.: contratos, DTOs, entidades), emita-a como subtarefa própria e referencie-a nas " +
        "dependentes. Cada prompt deve trazer interfaces e contratos explícitos para fluir sem " +
        "espera entre trilhas. O executor gera cerca de {0} tok/s: estime os minutos de cada " +
        "tarefa no campo etaMin. Responda SOMENTE com JSON, sem markdown nem texto extra: " +
        "{\"tasks\":[{\"title\":\"verbo de ação curto\",\"prompt\":\"instrução completa para o executor\",\"etaMin\":3}]}.";

    // Executor: enunciado rígido (1 tarefa, artefato + evidência, sem conversa).
    // Catálogo explícito: o 3B inventa tools se não souber os nomes válidos.
    private const string ExecutorSystem =
        "Você é o executor. Execute exatamente a tarefa recebida, de forma direta e técnica. " +
        "Responda com o artefato pedido seguido de evidência curta do que foi feito. " +
        "Ferramentas VÁLIDAS (use SOMENTE estas, via chamada de função): shell (comando), " +
        "read (path, offset, limit), write (path, content), edit (path, oldString, newString), " +
        "grep (pattern, path, include), glob (pattern, path). É PROIBIDO inventar outras " +
        "ferramentas (como build, copy, expose): se nenhuma servir, responda em prosa. " +
        "Sem conversa, sem perguntas de volta.";

    // Orch: síntese final em pt-BR a partir dos resultados.
    private const string SynthesisSystem =
        "Você é o orquestrador. Sintetize os resultados dos executores abaixo em resposta " +
        "final direta ao usuário, em pt-BR. Se algum executor falhou, diga o que faltou.";

    // Orch: reescreve enunciado que falhou, mais restrito e à prova do erro visto.
    private const string RefineSystem =
        "Você é o orquestrador. A subtarefa abaixo FALHOU no executor; reescreva o enunciado " +
        "de forma mais restrita e à prova do erro, mantendo o mesmo objetivo. Responda SOMENTE " +
        "com o novo prompt (texto puro, sem JSON nem markdown).";

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

        var tasks = await DecomposeAsync(agents.Orch, agents.Exec, text, ct);
        await sessions.AddMessageAsync(sessionId, "assistant", DivisionText(tasks, ExecTps(agents.Exec)), "", null, null);
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
        var tasks = await DecomposeAsync(agents.Orch, agents.Exec, text, ct);
        await sessions.AddMessageAsync(sessionId, "assistant", DivisionText(tasks, ExecTps(agents.Exec)), "", null, null);
        await sessions.TouchAsync(sessionId);
        return new DecomposeResult(true, tasks, "");
    }

    // Fase 2: executa tarefas aprovadas + sintetiza (sem redecompor).
    public async Task<OrchestrateResult> RunApprovedAsync(int sessionId, int userId, List<SubTask> tasks, CancellationToken ct, bool synthesize = true)
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
            lastUser?.Content ?? "", tasks, ct, synthesize);
    }

    // Fase 3: sintetiza a partir do rastro persistido (para rodar com outro
    // modelo em memória após os executores).
    public async Task<OrchestrateResult> SynthesizeAsync(int sessionId, int userId, CancellationToken ct)
    {
        var session = await sessions.GetAsync(sessionId, userId);
        if (session is null) return new OrchestrateResult(false, "", "Sessão não encontrada", 0);

        var agents = await ResolveAgentsAsync(session);
        if (agents is null) return new OrchestrateResult(false, "", "Orquestrador ou executor inválido", 0);

        var history = await sessions.GetMessagesAsync(sessionId, 200);
        var lastUser = history.LastOrDefault(m => m.Role == "user");
        var decomp = history.Select(m => m.Content ?? "")
            .LastOrDefault(c => c.Contains("[Orquestrador] Dividi em"));
        if (decomp is null) return new OrchestrateResult(false, "", "Sem decomposição persistida", 0);
        // O preview persistido é texto humano ("1. Título"); prompts não são
        // necessários na síntese (só títulos + resultados).
        var titles = decomp.Split('\n')
            .Select(l =>
            {
                var m = System.Text.RegularExpressions.Regex.Match(l.Trim(), @"^(\d+)\.\s*(.+)$");
                return m.Success ? m.Groups[2].Value.Trim() : null;
            })
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Cast<string>()
            .ToList();
        if (titles.Count == 0) return new OrchestrateResult(false, "", "Sem tarefas parseáveis", 0);
        var tasks = titles.Select(t => new SubTask(t, "")).ToList();

        var results = tasks.Select((t, i) =>
        {
            var done = history.Select(m => m.Content ?? "")
                .LastOrDefault(c => c.Contains($"[Executor {i + 1}] Concluído"));
            return done is null ? "[sem resultado persistido]" : done;
        }).ToList();
        var dump = string.Join("\n\n", tasks.Select((t, i) => $"## Tarefa {i + 1}: {t.Title}\n{results[i]}"));
        var synth = await BridgeHelper.ChatAsync(bridge, agents.Orch.BridgeUrl, agents.Orch.Model,
            PlanTemp(agents.Orch), SynthesisSystem,
            new JsonArray { new JsonObject { ["role"] = "user", ["content"] = $"Pedido: {lastUser?.Content ?? ""}\n\nResultados:\n{dump}" } }, ct);
        if (!synth.Ok) return new OrchestrateResult(false, "", $"Síntese falhou: {synth.Error}", tasks.Count);

        await sessions.AddMessageAsync(sessionId, "assistant", synth.Content,
            synth.Thinking, synth.PromptTokens, synth.CompletionTokens);
        await sessions.TouchAsync(sessionId);
        return new OrchestrateResult(true, synth.Content, "", tasks.Count);
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

    private static double ExecTps(Agent exec) => exec.MeasuredTps > 0 ? exec.MeasuredTps : 7;

    private static string DivisionText(List<SubTask> tasks, double tps)
    {
        string total = tasks.Sum(t => t.EtaMin) > 0 ? $" (total ~{tasks.Sum(t => t.EtaMin):0.#} min a {tps:0.#} tok/s)" : "";
        return "[Orquestrador] Dividi em " + tasks.Count + " tarefa(s)" + total + ":\n" +
            string.Join("\n", tasks.Select((t, i) => $"{i + 1}. {t.Title}" + (t.EtaMin > 0 ? $" (~{t.EtaMin:0.#} min)" : "")));
    }

    private async Task<OrchestrateResult> RunTasksAsync(int sessionId, Session session,
        Agent orch, Agent exec, string text, List<SubTask> tasks, CancellationToken ct, bool synthesize = true)
    {

        var results = new string[tasks.Count];
        using var gate = new SemaphoreSlim(_maxParallel);
        await Task.WhenAll(tasks.Select(async (task, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await sessions.AddMessageAsync(sessionId, "assistant",
                    $"[Executor {i + 1}] Iniciado: {task.Title}", "", null, null);
                results[i] = await RunExecutorTaskAsync(sessionId, i, task, orch, exec, ct);
                await sessions.AddMessageAsync(sessionId, "assistant",
                    $"[Executor {i + 1}] Concluído: {task.Title}\n{Truncate(results[i], 2000)}", "", null, null);
            }
            finally
            {
                gate.Release();
            }
        }));

        var dump = string.Join("\n\n", tasks.Select((t, i) => $"## Tarefa {i + 1}: {t.Title}\n{results[i]}"));
        if (!synthesize)
        {
            await sessions.TouchAsync(sessionId);
            return new OrchestrateResult(true, "", "", tasks.Count);
        }
        var synth = await BridgeHelper.ChatAsync(bridge, orch.BridgeUrl, orch.Model,
            PlanTemp(orch), SynthesisSystem,
            new JsonArray { new JsonObject { ["role"] = "user", ["content"] = $"Pedido: {text}\n\nResultados:\n{dump}" } }, ct);
        if (!synth.Ok) return new OrchestrateResult(false, "", $"Síntese falhou: {synth.Error}", tasks.Count);

        await sessions.AddMessageAsync(sessionId, "assistant", synth.Content,
            synth.Thinking, synth.PromptTokens, synth.CompletionTokens);
        await sessions.TouchAsync(sessionId);
        return new OrchestrateResult(true, synth.Content, "", tasks.Count);
    }

    private async Task<List<SubTask>> DecomposeAsync(Agent orch, Agent exec, string text, CancellationToken ct)
    {
        string system = DecomposeSystem.Replace("{0}", (exec.MeasuredTps > 0 ? exec.MeasuredTps : 7).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
        // Critério de aceite: >= MinTasks tarefas distintas; tenta até MaxDecomposeTries.
        for (int attempt = 1; attempt <= MaxDecomposeTries; attempt++)
        {
            string ask = attempt == 1 ? text
                : text + $"\n\nSua decomposição anterior foi insuficiente (tente de novo com MAIS granularidade: tentativa {attempt}).";
            var r = await BridgeHelper.ChatAsync(bridge, orch.BridgeUrl, orch.Model, PlanTemp(orch),
                system,
                new JsonArray { new JsonObject { ["role"] = "user", ["content"] = ask } }, ct);
            var tasks = ParseTasks(r.Ok ? r.Content : "");
            if (tasks.Count >= MinTasks && tasks.Select(t => t.Prompt).Distinct().Count() == tasks.Count)
                return tasks;
        }
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
                    o!["prompt"]!.GetValue<string>(),
                    o!["etaMin"]?.GetValue<double>() ?? 0))
                .Take(MaxTasks)
                .ToList();
        }
        catch
        {
            return new List<SubTask>();
        }
    }

    // Executor com tools no modo build (loop como o Stream); no modo plan, texto puro.
    // Ciclo com retry: falha sem conteúdo útil volta ao orch para refinamento
    // (máx. Orchestration:MaxTaskRetries), com re-disparo do enunciado corrigido.
    private async Task<string> RunExecutorTaskAsync(int sessionId, int index, SubTask task, Agent orch, Agent exec, CancellationToken ct)
    {
        string prompt = task.Prompt;
        for (int attempt = 0; attempt <= _maxTaskRetries; attempt++)
        {
            string result = await AttemptExecutorTaskAsync(sessionId, index, prompt, exec, ct);
            if (!IsFailure(result) || attempt == _maxTaskRetries) return result;
            await sessions.AddMessageAsync(sessionId, "assistant",
                $"[Orquestrador] Refinando tarefa {index + 1} após falha (tentativa {attempt + 1})...", "", null, null);
            var refined = await BridgeHelper.ChatAsync(bridge, orch.BridgeUrl, orch.Model,
                PlanTemp(orch), RefineSystem,
                new JsonArray { new JsonObject { ["role"] = "user", ["content"] = $"Tarefa: {task.Title}\nEnunciado: {prompt}\nEvidência da falha:\n{Truncate(result, 1000)}" } }, ct);
            if (!refined.Ok || string.IsNullOrWhiteSpace(refined.Content)) return result;
            prompt = refined.Content.Trim();
        }
        return $"[Executor {index + 1}] FALHOU após {_maxTaskRetries + 1} tentativa(s)";
    }

    private static bool IsFailure(string result) =>
        string.IsNullOrWhiteSpace(result) || result.Contains("[Executor ") && result.Contains("FALHOU");

    private async Task<string> AttemptExecutorTaskAsync(int sessionId, int index, string prompt, Agent exec, CancellationToken ct)
    {
        bool build = (exec.Mode ?? "build") != "plan";
        var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt } };
        var collected = new System.Text.StringBuilder();
        for (int turn = 0; turn < (build ? MaxToolTurns : 1); turn++)
        {
            var t = await BridgeHelper.ChatTurnAsync(bridge, exec.BridgeUrl, exec.Model,
                PlanTemp(exec), ExecutorSystem, history,
                build ? ToolDefinitions.GetTools(exec.Mode) : null, ct);
            if (!t.Ok) return $"[Executor {index + 1}] FALHOU: {t.Error}";
            collected.Append(t.Content);
            if (t.Calls.Count == 0)
            {
                // Recuperação tolerante: 3B escreve pseudo-tool em texto em vez de
                // chamar; se o bloco cita tool conhecida, executa de verdade 1x.
                if (build && TryRecoverPseudoTool(t.Content, out string rname, out string rargs))
                {
                    await sessions.AddMessageAsync(sessionId, "assistant",
                        $"[Executor {index + 1}] Recuperado bloco {rname} do texto; executando...", "", null, null);
                    var recovered = tools.Execute(rname, rargs, 120, exec.Mode);
                    string rcontent = recovered.Success ? recovered.Output : $"Error: {recovered.Error}";
                    if (rcontent.Length > 2000) rcontent = rcontent[..2000] + "\n[truncado]";
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = t.Content });
                    history.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = $"recovered-{index}-{turn}",
                        ["content"] = rcontent,
                    });
                    continue;
                }
                break;
            }
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

    // Detecta ```json {"name":"<tool conhecida>","arguments":{...}|"..."} ``` no texto.
    public static bool TryRecoverPseudoTool(string content, out string name, out string args)
    {
        name = "";
        args = "";
        if (string.IsNullOrEmpty(content)) return false;
        var known = new HashSet<string>(StringComparer.Ordinal) { "shell", "read", "write", "edit", "grep", "glob" };
        foreach (Match m in Regex.Matches(content, "```(?:json)?\\s*(\\{.+?\\})\\s*```", RegexOptions.Singleline))
        {
            JsonObject? obj;
            try { obj = JsonNode.Parse(m.Groups[1].Value)?.AsObject(); }
            catch { continue; }
            string n = obj?["name"]?.GetValue<string>() ?? "";
            if (!known.Contains(n)) continue;
            var a = obj?["arguments"];
            name = n;
            args = a is JsonObject ? a.ToJsonString() : a?.GetValue<string>() ?? "";
            return true;
        }
        return false;
    }

    public sealed record SubTask(string Title, string Prompt, double EtaMin = 0);
}
