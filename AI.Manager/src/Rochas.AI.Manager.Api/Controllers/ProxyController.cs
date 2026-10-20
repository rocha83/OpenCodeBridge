using Microsoft.AspNetCore.Authorization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Helpers;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.Data.Specification.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("v1")]
public sealed class ProxyController : ControllerBase
{
    private readonly IHttpClientFactory _http;
    private readonly IModelBindingService _bindings;
    private readonly IAgentService _agents;
    private readonly IGenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessRun> _runs;
    public ProxyController(IHttpClientFactory http, IModelBindingService bindings, IAgentService agents, IGenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessRun> runs) { _http = http; _bindings = bindings; _agents = agents; _runs = runs; }

    [HttpPost("chat/completions")]
    public async Task<IActionResult> ChatCompletions([FromBody] JsonElement body, CancellationToken ct)
    {
        // Auth (API key)
        var auth = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return Unauthorized(new { error = "missing bearer token" });
        var list = await _bindings.ListAsync(ct);
        var binding = list.FirstOrDefault();
        if (binding is null) return Problem("No model binding configured.");

        // Resolve profile por role (body.model pode ser nome de agente)
        string? modelName = null;
        if (body.TryGetProperty("model", out var mp) && mp.ValueKind == JsonValueKind.String) modelName = mp.GetString();
        Rochas.AI.Manager.Domain.Entities.Agent? agent = null;
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var agents = await _agents.ListAsync(ct);
            agent = agents.FirstOrDefault(a => a.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase) || a.Model.Equals(modelName, StringComparison.OrdinalIgnoreCase));
        }
        // inject temperature/thinking into body if agent found
        if (agent is not null)
        {
            try
            {
                var node = JsonNode.Parse(body.GetRawText())?.AsObject();
                if (node is not null)
                {
                    node["temperature"] = agent.Temperature;
                    body = JsonDocument.Parse(node.ToJsonString()).RootElement;
                }
            }
            catch { }
        }

        // Context guard (0.95 threshold)
        int estTokens = body.GetRawText().Length / 4;
        int threshold = (int)(binding.ContextWindow * 0.95);
        if (estTokens > threshold)
        {
            Response.StatusCode = 413;
            return Content($"{{\"error\":\"context exceeded: {estTokens} > {threshold}\"}}", "application/json");
        }
        var baseUrl = binding.Provider.Equals("llama", StringComparison.OrdinalIgnoreCase) ? "http://localhost:4125" : "http://localhost:4124";
        var url = $"{baseUrl}/v1/chat/completions";

        bool stream = body.TryGetProperty("stream", out var sp) && sp.ValueKind == JsonValueKind.True;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var client = _http.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.GetRawText(), System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "guest@123");
        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

        if (stream)
        {
            Response.StatusCode = (int)resp.StatusCode;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            await using var upstream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(upstream);
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                await Response.WriteAsync(line + "\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            return new EmptyResult();
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        json = ReasoningSanitizer.Sanitize(json);
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is not null)
            {
                var coerced = ToolCallCoercer.CoerceChatCalls(root);
                if (coerced > 0) json = root.ToJsonString();
            }
        }
        catch { }

        sw.Stop();
        try
        {
            var run = new Rochas.AI.Manager.Domain.Harness.HarnessRun
            {
                Name = "proxy",
                EvidenceId = 0,
                ProfileVersionId = 0,
                ModelBindingId = binding.Id,
                ResultCode = (int)resp.StatusCode,
                DurationMs = (int)sw.ElapsedMilliseconds,
                TokensPerSecond = json.Length / 4.0 / Math.Max(0.001, sw.Elapsed.TotalSeconds)
            };
            await _runs.Add(run);
        }
        catch { }
        Response.StatusCode = (int)resp.StatusCode;
        return Content(json, "application/json");
    }
}
