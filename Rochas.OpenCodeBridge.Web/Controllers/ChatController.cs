using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.DapperRepository;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// Chat: tela + proxy de stream p/ bridge (contorna falta de CORS no HttpListener).
[Authorize]
public sealed class ChatController(
    GenericRepository<Agent> agents,
    SessionService sessionService,
    BridgeClient bridge,
    ToolExecutor toolExecutor,
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
        var agent = await agents.Get(new Agent { Id = agentId });
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
        return Json(list.Select(s => new { s.Id, s.AgentId, s.Title, s.CreatedAt, s.UpdatedAt }));
    }

    [HttpPost("/Chat/Sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest req)
    {
        var agent = await agents.Get(new Agent { Id = req.AgentId });
        if (agent is null || !agent.Active) return BadRequest("Agente inválido");
        var title = string.IsNullOrWhiteSpace(req.Title) ? "Nova sessão" : req.Title;
        var s = await sessionService.CreateAsync(CurrentUserId, req.AgentId, title);
        return Json(new { s.Id, s.AgentId, s.Title, s.CreatedAt });
    }

    [HttpGet("/Chat/Sessions/{id:int}")]
    public async Task<IActionResult> GetSession(int id)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        return Json(new { s.Id, s.AgentId, s.Title, s.CreatedAt, s.UpdatedAt });
    }

    [HttpGet("/Chat/Sessions/{id:int}/Messages")]
    public async Task<IActionResult> GetSessionMessages(int id, int limit = 50)
    {
        var s = await sessionService.GetAsync(id, CurrentUserId);
        if (s is null) return NotFound();
        var msgs = await sessionService.GetMessagesAsync(id, limit);
        return Json(msgs.Select(m => new { m.Id, m.Role, m.Content, m.Thinking, m.PromptTokens, m.CompletionTokens, m.CreatedAt }));
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
        var agent = (await agents.Query(new Agent { Id = req.AgentId })).FirstOrDefault();
        if (agent is null || !agent.Active) return BadRequest("Agente inválido");
        await sessionService.UpdateAgentAsync(id, req.AgentId);
        return Ok();
    }

    public sealed class CreateSessionRequest
    {
        public int AgentId { get; set; }
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

    // ---- Stream com sessão ----

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
            agent = await agents.Get(new Agent { Id = session.AgentId });
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

        // Stream com captura da resposta do assistant
        var assistantContent = new System.Text.StringBuilder();
        var assistantThinking = new System.Text.StringBuilder();
        int? promptTokens = null;
        int? completionTokens = null;

        var outputStream = Response.Body;
        var buffer = new System.IO.MemoryStream();
        var (ok, error) = await bridge.StreamAsync(agent.BridgeUrl, agent.Model, agent.Temperature,
            agent.SystemPrompt, messages, buffer, ct);

        // Processa buffer: repassa para Response.Body e captura assistant content + usage
        buffer.Position = 0;
        using var reader = new System.IO.StreamReader(buffer);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            // Repassa linha para cliente
            var lineBytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            await outputStream.WriteAsync(lineBytes, ct);
            await outputStream.FlushAsync(ct);

            // Parsa SSE para capturar assistant content/thinking/usage
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
                    }
                    // usage vem no último chunk (ou em chunk separado)
                    if (chunk?["usage"] is JsonObject usage)
                    {
                        promptTokens = usage["prompt_tokens"]?.GetValue<int>();
                        completionTokens = usage["completion_tokens"]?.GetValue<int>();
                    }
                }
                catch { /* ignora parse errors */ }
            }
        }

        // Persiste resposta do assistant se há sessão
        if (ok && session is not null && session.Id.HasValue && assistantContent.Length > 0)
        {
            await sessionService.AddMessageAsync(session.Id.Value, "assistant", assistantContent.ToString(), assistantThinking.ToString(), promptTokens, completionTokens);
            await sessionService.TouchAsync(session.Id.Value);
        }

        if (!ok && !Response.HasStarted) Response.StatusCode = 502;
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