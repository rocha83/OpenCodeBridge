using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.DapperRepository;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// Chat: tela + proxy de stream p/ bridge (contorna falta de CORS no HttpListener).
[Authorize]
public sealed class ChatController(GenericRepository<Agent> agents, BridgeClient bridge) : Controller
{
    public async Task<IActionResult> Index()
    {
        var list = (await agents.Query(new Agent())).Where(a => a.Active).ToList();
        ViewBag.Agents = list;
        return View();
    }

    public sealed class ChatRequest
    {
        public int AgentId { get; set; }
        public List<ChatMsg> Messages { get; set; } = new();
    }

    public sealed class ChatMsg
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
    }

    [HttpPost]
    public async Task Stream([FromBody] ChatRequest req, CancellationToken ct)
    {
        var agent = await agents.Get(new Agent { Id = req.AgentId });
        if (agent is null) { Response.StatusCode = 404; return; }
        var messages = new JsonArray();
        foreach (var m in req.Messages.TakeLast(20))
            messages.Add(new JsonObject
            {
                ["role"] = m.Role is "assistant" or "system" ? m.Role : "user",
                ["content"] = m.Content ?? "",
            });
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        var (ok, error) = await bridge.StreamAsync(agent.BridgeUrl, agent.Model, agent.Temperature,
            agent.SystemPrompt, messages, Response.Body, ct);
        if (!ok && !Response.HasStarted) Response.StatusCode = 502;
    }
}
