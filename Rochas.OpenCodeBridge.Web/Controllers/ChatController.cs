using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// Chat: tela + proxy de stream p/ bridge (contorna falta de CORS no HttpListener).
[Authorize]
public sealed class ChatController(
    IGenericRepository<Agent> agents,
    IPersistenceRepository<Agent> agentsWrite,
    ISessionService sessionService,
    IToolExecutor toolExecutor,
    IHttpClientFactory http) : Controller
{
    private int CurrentUserId => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0");

    public async Task<IActionResult> Index()
    {
        var list = (await agents.Query(new Agent())).Where(a => a.Active).ToList();
        ViewBag.Agents = list;
        return View();
    }

    // Ping leve na bridge do agente p/ sinalizar conectividade da engine.
    [HttpGet("/Chat/Ping")]
    public async Task<IActionResult> Ping(int agentId)
    {
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == agentId);
        if (agent is null) return Json(new { ok = false });
        try
        {
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var r = await client.GetAsync(agent.BridgeUrl.TrimEnd('/') + "/api/status");
            return Json(new { ok = r.IsSuccessStatusCode });
        }
        catch
        {
            return Json(new { ok = false });
        }
    }

    // ---- Sessions CRUD ----

    [HttpGet("/Chat/Sessions")]
    public async Task<IActionResult> GetSessions()
    {
        var list = await sessionService.GetByUserAsync(CurrentUserId);
        return Json(list.Select(s => new { s.Id, s.AgentId, s.ExecutorAgentId, s.Title, s.CreatedAt, s.UpdatedAt }));
    }

    [HttpPost("/Chat/Sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == req.AgentId);
        if (agent is null || !agent.Active) return BadRequest("Agente inválido");
        int? execId = req.ExecutorAgentId;
        if (execId.HasValue)
        {
            if (execId.Value == req.AgentId) return BadRequest("Executor deve diferir do orquestrador");
            var exec = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == execId.Value);
            if (exec is null || !exec.Active) return BadRequest("Executor inválido");
            if (Agent.EffectiveMode(exec) != Agent.EffectiveMode(agent))
                return BadRequest("Orquestrador e executor devem estar no mesmo modo (plan ou build)");
        }
        var title = string.IsNullOrWhiteSpace(req.Title) ? "Nova sessão" : req.Title;
        var s = await sessionService.CreateAsync(CurrentUserId, req.AgentId, title, execId);
        return Json(new { s.Id, s.AgentId, s.ExecutorAgentId, s.Title, s.CreatedAt });
    }

    [HttpGet("/Chat/Sessions/{id:int}")]
    public async Task<IActionResult> GetSession(int id)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        return Json(new { s.Id, s.AgentId, s.ExecutorAgentId, s.Title, s.CreatedAt, s.UpdatedAt });
    }

    [HttpGet("/Chat/Sessions/{id:int}/Messages")]
    public async Task<IActionResult> GetSessionMessages(int id, int limit = 50)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var msgs = await sessionService.GetMessagesAsync(id, limit);
        return Json(msgs.Select(m => new { m.Id, m.Role, m.Content, m.Thinking, m.PromptTokens, m.CompletionTokens, m.CreatedAt }));
    }

    // GET /Chat/Sessions/{id}/Tasks - painel de acompanhamento da decomposição
    // (derivado das mensagens marcadas; sem tabela nova).
    [HttpGet("/Chat/Sessions/{id:int}/Tasks")]
    public async Task<IActionResult> GetSessionTasks(int id)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var msgs = await sessionService.GetMessagesAsync(id, 200);
        var items = msgs.Select(m => (m.Role, m.Content)).ToList();
        var tasks = SessionTaskPanel.Parse(items);
        return Json(new
        {
            tasks = tasks.Select(t => new { index = t.Index, title = t.Title, status = t.Status, etaMin = t.EtaMin }),
            totalEtaMin = Math.Round(tasks.Sum(t => t.EtaMin), 1),
            synthesized = SessionTaskPanel.Synthesized(items, tasks.Count),
        });
    }

    [HttpDelete("/Chat/Sessions/{id:int}")]
    public async Task<IActionResult> DeleteSession(int id)
    {
        await sessionService.DeleteAsync(id, CurrentUserId);
        return Ok();
    }

    [HttpPut("/Chat/Sessions/{id:int}/Title")]
    public async Task<IActionResult> UpdateSessionTitle(int id, [FromBody] UpdateTitleRequest req)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        await sessionService.UpdateTitleAsync(id, req.Title);
        return Ok();
    }

    [HttpPut("/Chat/Sessions/{id:int}/Agent")]
    public async Task<IActionResult> UpdateSessionAgent(int id, [FromBody] UpdateAgentRequest req)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == req.AgentId);
        if (agent is null || !agent.Active) return BadRequest("Agente inválido");
        await sessionService.UpdateAgentAsync(id, req.AgentId);
        return Ok();
    }

    [HttpPut("/Chat/Sessions/{id:int}/Executor")]
    public async Task<IActionResult> UpdateSessionExecutor(int id, [FromBody] UpdateExecutorRequest req)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        if (req.ExecutorAgentId.HasValue)
        {
            if (req.ExecutorAgentId.Value == s.AgentId) return BadRequest("Executor deve diferir do orquestrador");
            var exec = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == req.ExecutorAgentId.Value);
            if (exec is null || !exec.Active) return BadRequest("Executor inválido");
            var orch = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == s.AgentId);
            if (orch is not null && Agent.EffectiveMode(exec) != Agent.EffectiveMode(orch))
                return BadRequest("Orquestrador e executor devem estar no mesmo modo (plan ou build)");
        }
        await sessionService.UpdateExecutorAsync(id, req.ExecutorAgentId);
        return Ok();
    }

    // POST /Chat/Orchestrate - pipeline híbrido (orch decompõe, executores em paralelo).
    [HttpPost("/Chat/Orchestrate")]
    public async Task<IActionResult> Orchestrate([FromBody] OrchestrateRequest req, [FromServices] IOrchestrationService orch, CancellationToken ct)
    {
        if (req.SessionId <= 0 || string.IsNullOrWhiteSpace(req.Text))
            return BadRequest("Sessão e texto obrigatórios");
        var s = await sessionService.GetAsync(req.SessionId, CurrentUserId);
        if (s is null) return NotFound();
        if (!s.ExecutorAgentId.HasValue) return BadRequest("Sessão sem executor selecionado");
        var result = await orch.OrchestrateAsync(req.SessionId, CurrentUserId, req.Text, ct);
        if (!result.Ok) return BadRequest(result.Error);
        return Json(new { synthesis = result.Synthesis, taskCount = result.TaskCount });
    }

    // POST /Chat/Decompose - fase 1: só decompõe e persiste o preview (pipe só após aprovar).
    [HttpPost("/Chat/Decompose")]
    public async Task<IActionResult> Decompose([FromBody] OrchestrateRequest req, [FromServices] IOrchestrationService orch, CancellationToken ct)
    {
        if (req.SessionId <= 0 || string.IsNullOrWhiteSpace(req.Text))
            return BadRequest("Sessão e texto obrigatórios");
        var s = await sessionService.GetAsync(req.SessionId, CurrentUserId);
        if (s is null) return NotFound();
        if (!s.ExecutorAgentId.HasValue) return BadRequest("Sessão sem executor selecionado");
        var result = await orch.PreviewAsync(req.SessionId, CurrentUserId, req.Text, ct);
        if (!result.Ok) return BadRequest(result.Error);
        return Json(new { tasks = result.Tasks.Select(t => new { title = t.Title, prompt = t.Prompt, etaMin = t.EtaMin, needsTools = t.NeedsTools }) });
    }

    // POST /Chat/OrchestrateApproved - fase 2: executa tarefas aprovadas + sintetiza.
    [HttpPost("/Chat/OrchestrateApproved")]
    public async Task<IActionResult> OrchestrateApproved([FromBody] OrchestrateApprovedRequest req, [FromServices] IOrchestrationService orch, CancellationToken ct)
    {
        if (req.SessionId <= 0 || req.Tasks is null || req.Tasks.Count == 0)
            return BadRequest("Sessão e tarefas aprovadas obrigatórias");
        var s = await sessionService.GetAsync(req.SessionId, CurrentUserId);
        if (s is null) return NotFound();
        if (!s.ExecutorAgentId.HasValue) return BadRequest("Sessão sem executor selecionado");
        if (req.Tasks.Count > 16) return BadRequest("Máximo 16 tarefas");
        var tasks = req.Tasks
            .Where(t => !string.IsNullOrWhiteSpace(t.Prompt))
            .Select(t => new OrchestrationService.SubTask(
                string.IsNullOrWhiteSpace(t.Title) ? "Tarefa" : t.Title.Trim(), t.Prompt, t.EtaMin, t.NeedsTools))
            .ToList();
        var result = await orch.RunApprovedAsync(req.SessionId, CurrentUserId, tasks, ct, req.Synthesize);
        if (!result.Ok) return BadRequest(result.Error);
        return Json(new { synthesis = result.Synthesis, taskCount = result.TaskCount });
    }

    // POST /Chat/Synthesize - fase 3: sintetiza do rastro (outro modelo em memória).
    [HttpPost("/Chat/Synthesize")]
    public async Task<IActionResult> Synthesize([FromBody] SynthesizeRequest req, [FromServices] IOrchestrationService orch, CancellationToken ct)
    {
        if (req.SessionId <= 0) return BadRequest("Sessão obrigatória");
        var s = await sessionService.GetAsync(req.SessionId, CurrentUserId);
        if (s is null) return NotFound();
        var result = await orch.SynthesizeAsync(req.SessionId, CurrentUserId, ct);
        if (!result.Ok) return BadRequest(result.Error);
        return Json(new { synthesis = result.Synthesis, taskCount = result.TaskCount });
    }

    // POST /Chat/Executors/{id}/Probe - mede tok/s do executor e grava em measured_tps.
    [HttpPost("/Chat/Executors/{id:int}/Probe")]
    public async Task<IActionResult> ProbeExecutor(int id, [FromServices] IBridgeClient bridge, CancellationToken ct)
    {
        var agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == id);
        if (agent is null || !agent.Active) return NotFound();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await BridgeHelper.ChatAsync(bridge, agent.BridgeUrl, agent.Model, agent.Temperature,
            "Responda somente: ok",
            new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Responda somente: ok" } }, ct);
        sw.Stop();
        int total = (r.PromptTokens ?? 0) + (r.CompletionTokens ?? 0);
        if (total <= 0) total = Math.Max(1, r.Content.Length / 4);
        if (!r.Ok || sw.Elapsed.TotalSeconds <= 0)
            return BadRequest("Sonda falhou: " + r.Error);
        double tps = total / sw.Elapsed.TotalSeconds;
        agent.MeasuredTps = Math.Round(tps, 1);
        await agentsWrite.Update(agent, new Agent { Id = agent.Id });
        return Json(new { tps = agent.MeasuredTps });
    }

    public sealed class OrchestrateRequest
    {
        public int SessionId { get; set; }
        public string Text { get; set; } = "";
    }

    public sealed class OrchestrateApprovedRequest
    {
        public int SessionId { get; set; }
        public List<ApprovedTask> Tasks { get; set; } = new();
        public bool Synthesize { get; set; } = true;
    }

    public sealed class SynthesizeRequest
    {
        public int SessionId { get; set; }
    }

    public sealed class ApprovedTask
    {
        public string Title { get; set; } = "";
        public string Prompt { get; set; } = "";
        public double EtaMin { get; set; }
        public bool NeedsTools { get; set; }
    }

    public sealed class CreateSessionRequest
    {
        public int AgentId { get; set; }
        public int? ExecutorAgentId { get; set; }
        public string Title { get; set; } = "";
    }

    public sealed class UpdateTitleRequest
    {
        public string Title { get; set; } = "";
    }

    public sealed class UpdateAgentRequest
    {
        public int AgentId { get; set; }
    }

    public sealed class UpdateExecutorRequest
    {
        public int? ExecutorAgentId { get; set; }
    }

    // ---- Stream com sessão + tool calling ----

    public sealed class StreamRequest
    {
        public int? SessionId { get; set; }
        public int AgentId { get; set; }
        public List<ChatMsg> Messages { get; set; } = new();
    }

    public sealed class ChatMsg
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
    }

    [HttpPost("/Chat/Stream")]
    public async Task Stream([FromBody] StreamRequest req, CancellationToken ct)
    {
        Agent agent;
        Session? session = null;
        List<SessionMessage> history = new();

        if (req.SessionId.HasValue)
        {
            session = await sessionService.GetAsync(req.SessionId.Value, CurrentUserId);
            if (session is null) { Response.StatusCode = 404; return; }
            agent = (await agents.Query(new Agent())).FirstOrDefault(a => a.Id == session.AgentId);
            if (agent is null || !agent.Active) { Response.StatusCode = 404; return; }
            history = await sessionService.GetMessagesAsync(req.SessionId.Value);
        }
        else
        {
            agent = await agents.Get(new Agent { Id = req.AgentId });
            if (agent is null || !agent.Active) { Response.StatusCode = 404; return; }
        }

        // Monta mensagens para a bridge
        var messages = new JsonArray();
        // NÃO adiciona system prompt aqui — o BridgeClient já faz isso via parâmetro

        // Contexto com janela de tokens
        var context = ContextWindow.BuildContext(history);
        foreach (var m in context)
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        // Mensagens novas do request (se sem sessão) ou última mensagem do usuário
        if (!req.SessionId.HasValue)
        {
            foreach (var m in req.Messages.TakeLast(20))
                messages.Add(new JsonObject { ["role"] = m.Role is "assistant" or "system" ? m.Role : "user", ["content"] = m.Content ?? "" });
        }
        else if (req.Messages.Count > 0)
        {
            var last = req.Messages.Last();
            messages.Add(new JsonObject { ["role"] = last.Role is "assistant" or "system" ? last.Role : "user", ["content"] = last.Content ?? "" });
        }

        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";

        // Persiste mensagem do usuário se há sessão
        if (session is not null && req.Messages.Count > 0 && session.Id.HasValue)
        {
            var lastUser = req.Messages.Last(m => m.Role == "user");
            await sessionService.AddMessageAsync(session.Id.Value, "user", lastUser.Content, "", null, null);
        }

        // Stream com tool calling loop
        await StreamWithTools(agent, messages, session, ct);
    }

    private async Task StreamWithTools(Agent agent, JsonArray messages, Session? session, CancellationToken ct)
    {
        var outputStream = Response.Body;
        var client = http.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        var assistantContent = new System.Text.StringBuilder();
        var assistantThinking = new System.Text.StringBuilder();
        int? promptTokens = null;
        int? completionTokens = null;

        // Helper to build fresh messages array from values (avoids JsonNode parent issues)
        JsonArray BuildMessages(JsonArray src)
        {
            var dst = new JsonArray();
            foreach (var node in src)
            {
                if (node is JsonObject obj)
                {
                    var clone = new JsonObject();
                    foreach (var kv in obj)
                    {
                        var val = kv.Value;
                        if (val is JsonArray arr)
                        {
                            // Rebuild array from values
                            var newArr = new JsonArray();
                            foreach (var item in arr)
                            {
                                if (item is JsonObject itemObj)
                                {
                                    var itemClone = new JsonObject();
                                    foreach (var ikv in itemObj)
                                        itemClone[ikv.Key] = ikv.Value?.DeepClone();
                                    newArr.Add(itemClone);
                                }
                                else
                                {
                                    newArr.Add(item?.DeepClone());
                                }
                            }
                            clone[kv.Key] = newArr;
                        }
                        else
                        {
                            clone[kv.Key] = val?.DeepClone();
                        }
                    }
                    dst.Add(clone);
                }
            }
            return dst;
        }

        while (!ct.IsCancellationRequested)
        {
            // Modo plan: tools de leitura/navegação + temp 0.4.
            bool plan = agent.Mode == "plan";
            var tools = ToolDefinitions.GetTools(plan ? "plan" : null);
            var body = new JsonObject
            {
                ["model"] = agent.Model,
                ["messages"] = BuildMessages(messages),
                ["temperature"] = plan ? 0.4 : agent.Temperature,
                ["max_tokens"] = 2048,
                ["stream"] = true,
                ["tools"] = tools,
                ["tool_choice"] = "auto"
            };

            // Repasse ao vivo (sem buffer total): ResponseHeadersRead ou o PostAsync
            // espera o corpo inteiro antes de entregar qualquer linha.
            using var bridgeReq = new HttpRequestMessage(HttpMethod.Post, agent.BridgeUrl.TrimEnd('/') + "/v1/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var res = await client.SendAsync(bridgeReq, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!res.IsSuccessStatusCode)
            {
                if (!Response.HasStarted) Response.StatusCode = 502;
                return;
            }

            // Repasse ao vivo: cada linha da bridge vai ao cliente asim que chega
            // (sem CopyToAsync — buffer total matava o efeito "digitando").
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);
            string? line;
            // Accumulate tool calls by index (arguments split across chunks)
            var toolCallAccum = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
            string? finishReason = null;

            while ((line = await reader.ReadLineAsync()) != null)
            {
                // Segura o [DONE] da bridge: o DONE do cliente sai após persistir.
                bool isDone = line.StartsWith("data: ") && line[6..].Trim() == "[DONE]";
                if (!isDone)
                {
                    var lineBytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
                    await outputStream.WriteAsync(lineBytes, ct);
                    await outputStream.FlushAsync(ct);
                }

                // Parsa SSE para capturar assistant content/thinking/tool_calls/usage
                if (line.StartsWith("data: "))
                {
                    var data = line[6..].Trim();
                    if (data == "[DONE]") continue;
                    try
                    {
                        var chunk = JsonNode.Parse(data);
                        var choices = chunk?["choices"]?.AsArray();
                        if (choices?.Count > 0)
                        {
                            var delta = choices[0]?["delta"];
                            if (delta?["reasoning_content"]?.GetValue<string>() is string rc)
                                assistantThinking.Append(rc);
                            if (delta?["content"]?.GetValue<string>() is string cc)
                                assistantContent.Append(cc);

                            // Captura tool_calls
                            if (delta?["tool_calls"] is JsonArray tcArr)
                            {
                                foreach (var tc in tcArr)
                                {
                                    var tcObj = tc?.AsObject();
                                    if (tcObj is null) continue;
                                    var index = tcObj["index"]?.GetValue<int>() ?? 0;
                                    var id = tcObj["id"]?.GetValue<string>() ?? "";
                                    var fn = tcObj["function"]?.AsObject();
                                    if (fn is null) continue;
                                    var name = fn["name"]?.GetValue<string>() ?? "";
                                    var argsChunk = fn["arguments"]?.GetValue<string>() ?? "";
                                    
                                    if (!toolCallAccum.TryGetValue(index, out var existing))
                                    {
                                        existing = (id, name, new StringBuilder());
                                        toolCallAccum[index] = existing;
                                    }
                                    if (id.Length > 0) existing.Id = id;
                                    if (name.Length > 0) existing.Name = name;
                                    existing.Args.Append(argsChunk);
                                }
                            }

                            finishReason = choices[0]?["finish_reason"]?.GetValue<string>();
                        }
                        if (chunk?["usage"] is JsonObject usage)
                        {
                            promptTokens = usage["prompt_tokens"]?.GetValue<int>();
                            completionTokens = usage["completion_tokens"]?.GetValue<int>();
                        }
                    }
                    catch { /* ignora parse errors */ }
                }
            }

            // Se não houve tool_calls ou finish_reason != tool_calls, terminamos
            if (toolCallAccum.Count == 0 || finishReason != "tool_calls")
            {
                break;
            }

            // Converte accumulated tool calls para lista
            var toolCalls = toolCallAccum.Values
                .Where(t => !string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Name))
                .Select(t => (t.Id, t.Name, t.Args.ToString()))
                .ToList();

            // Adiciona mensagem do assistant com tool_calls para o histórico (necessário para o modelo ver suas próprias chamadas)
            var assistantMsg = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = assistantContent.Length > 0 ? assistantContent.ToString() : "",
                ["tool_calls"] = new JsonArray()
            };
            var tcArray = (JsonArray)assistantMsg["tool_calls"]!;
            foreach (var (id, name, args) in toolCalls)
            {
                tcArray.Add(new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args }
                });
            }
            messages.Add(assistantMsg);

            // Executa cada tool e adiciona resultado às mensagens (teto 2000 chars:
            // output longo volta truncado para não estourar o contexto do loop).
            // Progresso vai ao cliente em tempo real para a UI indicar execução.
            foreach (var (id, name, args) in toolCalls)
            {
                await WriteProgressAsync(outputStream, "tool_start", name, null, ct);
                var result = toolExecutor.Execute(name, args, 120, agent.Mode);
                await WriteProgressAsync(outputStream, "tool_done", name, result.Success, ct);
                string content = result.Success ? result.Output : $"Error: {result.Error}";
                if (content.Length > 2000) content = content[..2000] + "\n[truncado]";
                var toolResult = new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = id,
                    ["content"] = content
                };
                messages.Add(toolResult);
            }

            // Reseta para próxima iteração
            assistantContent.Clear();
            assistantThinking.Clear();
        }

        // Persiste resposta final do assistant se há sessão
        if (session is not null && session.Id.HasValue && assistantContent.Length > 0)
        {
            await sessionService.AddMessageAsync(session.Id.Value, "assistant", assistantContent.ToString(), assistantThinking.ToString(), promptTokens, completionTokens);
            await sessionService.TouchAsync(session.Id.Value);
        }

        // DONE próprio após persistir: elimina race do cliente ler histórico cedo.
        var doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
        await outputStream.WriteAsync(doneBytes, ct);
        await outputStream.FlushAsync(ct);
    }

    // Evento de progresso ao cliente (a UI mostra "Executando [tool]..." / "[tool] executado").
    private static async Task WriteProgressAsync(System.IO.Stream output, string stage, string name, bool? ok, CancellationToken ct)
    {
        var evt = new JsonObject { ["progress"] = stage, ["name"] = name };
        if (ok.HasValue) evt["ok"] = ok.Value;
        var bytes = Encoding.UTF8.GetBytes("data: " + evt.ToJsonString() + "\n\n");
        await output.WriteAsync(bytes, ct);
        await output.FlushAsync(ct);
    }

    // POST /Chat/Tool - Executa tool no servidor
    [HttpPost("/Chat/Tool")]
    public async Task<IActionResult> ExecuteTool([FromBody] ToolRequest request)
    {
        var result = toolExecutor.Execute(request.Name, request.Arguments);
        return Json(new { success = result.Success, output = result.Output, error = result.Error });
    }

    public sealed class ToolRequest
    {
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }
}