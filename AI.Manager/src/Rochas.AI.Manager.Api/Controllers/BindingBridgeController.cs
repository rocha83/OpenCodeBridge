using Rochas.Data.Specification.Interfaces;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Helpers;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Route("v1")]
public sealed class BindingBridgeController : ControllerBase
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly IModelBindingService modelBindingService;
    private readonly IAgentService agentService;
    private readonly IGenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessRun> harnessRunRepository;

    public BindingBridgeController(
        IHttpClientFactory httpClientFactory,
        IModelBindingService modelBindingService,
        IAgentService agentService,
        IGenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessRun> harnessRunRepository)
    {
        this.httpClientFactory = httpClientFactory;
        this.modelBindingService = modelBindingService;
        this.agentService = agentService;
        this.harnessRunRepository = harnessRunRepository;
    }

    [HttpPost("chat/completions")]
    public async Task<IActionResult> ChatCompletions([FromBody] JsonElement requestBody, CancellationToken cancellationToken)
    {
        // Auth (API key)
        var authorizationHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new { error = "missing bearer token" });
        }

        var bindings = await modelBindingService.ListAsync(cancellationToken);
        var binding = bindings.FirstOrDefault();
        if (binding is null)
        {
            return Problem("No model binding configured.");
        }

        // Context guard (0.95 threshold)
        int estimatedTokens = requestBody.GetRawText().Length / 4;
        int contextThreshold = (int)(binding.ContextWindow * 0.95);
        if (estimatedTokens > contextThreshold)
        {
            Response.StatusCode = 413;
            return Content($"{{\"error\":\"context exceeded: {estimatedTokens} > {contextThreshold}\"}}", "application/json");
        }

        // Resolve profile por role (body.model pode ser nome de agente)
        string? modelName = null;
        if (requestBody.TryGetProperty("model", out var modelProperty) && modelProperty.ValueKind == JsonValueKind.String)
        {
            modelName = modelProperty.GetString();
        }
        Rochas.AI.Manager.Domain.Entities.Agent? agent = null;
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            var agents = await agentService.ListAsync(cancellationToken);
            agent = agents.FirstOrDefault(currentAgent =>
                currentAgent.Name.Equals(modelName, StringComparison.OrdinalIgnoreCase) ||
                currentAgent.Model.Equals(modelName, StringComparison.OrdinalIgnoreCase));
        }

        // inject temperature/thinking into body if agent found
        JsonElement effectiveBody = requestBody;
        if (agent is not null)
        {
            try
            {
                var node = JsonNode.Parse(requestBody.GetRawText())?.AsObject();
                if (node is not null)
                {
                    node["temperature"] = agent.Temperature;
                    effectiveBody = JsonDocument.Parse(node.ToJsonString()).RootElement;
                }
            }
            catch { }
        }

        var baseUrl = binding.Provider.Equals("llama", StringComparison.OrdinalIgnoreCase)
            ? "http://localhost:4125"
            : "http://localhost:4124";
        var upstreamUrl = $"{baseUrl}/v1/chat/completions";

        bool isStream = effectiveBody.TryGetProperty("stream", out var streamProperty) && streamProperty.ValueKind == JsonValueKind.True;

        var httpClient = httpClientFactory.CreateClient();
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new StringContent(effectiveBody.GetRawText(), System.Text.Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "guest@123");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var upstreamResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (isStream)
        {
            Response.StatusCode = (int)upstreamResponse.StatusCode;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(upstreamStream);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                await Response.WriteAsync(line + "\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
            return new EmptyResult();
        }

        var responseJson = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
        responseJson = ReasoningSanitizer.Sanitize(responseJson);
        try
        {
            var root = JsonNode.Parse(responseJson)?.AsObject();
            if (root is not null)
            {
                var coercedCount = ToolCallCoercer.CoerceChatCalls(root);
                if (coercedCount > 0)
                {
                    responseJson = root.ToJsonString();
                }
            }
        }
        catch { }

        stopwatch.Stop();
        try
        {
            var run = new Rochas.AI.Manager.Domain.Harness.HarnessRun
            {
                Name = "proxy",
                EvidenceId = 0,
                ProfileVersionId = 0,
                ModelBindingId = binding.Id,
                ResultCode = (int)upstreamResponse.StatusCode,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                TokensPerSecond = responseJson.Length / 4.0 / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)
            };
            await harnessRunRepository.Add(run);
        }
        catch { }

        Response.StatusCode = (int)upstreamResponse.StatusCode;
        return Content(responseJson, "application/json");
    }

    [HttpPost("responses")]
    public async Task<IActionResult> Responses([FromBody] JsonElement requestBody, CancellationToken cancellationToken)
    {
        var authorizationHeader = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new { error = "missing bearer token" });
        }

        var bindings = await modelBindingService.ListAsync(cancellationToken);
        var binding = bindings.FirstOrDefault();
        if (binding is null)
        {
            return Problem("No model binding configured.");
        }

        var baseUrl = binding.Provider.Equals("llama", StringComparison.OrdinalIgnoreCase)
            ? "http://localhost:4125"
            : "http://localhost:4124";
        var upstreamUrl = $"{baseUrl}/v1/responses";

        bool isStream = requestBody.TryGetProperty("stream", out var sp) && sp.ValueKind == JsonValueKind.True;

        var httpClient = httpClientFactory.CreateClient();
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new StringContent(requestBody.GetRawText(), System.Text.Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "guest@123");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var upstreamResponse = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (isStream)
        {
            Response.StatusCode = (int)upstreamResponse.StatusCode;
            Response.ContentType = "text/event-stream";
            Response.Headers.CacheControl = "no-cache";
            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(upstreamStream);
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                await Response.WriteAsync(line + "\n", cancellationToken);
                await Response.Body.FlushAsync(cancellationToken);
            }
            return new EmptyResult();
        }

        var responseJson = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
        stopwatch.Stop();
        try
        {
            var run = new Rochas.AI.Manager.Domain.Harness.HarnessRun
            {
                Name = "proxy-responses",
                EvidenceId = 0,
                ProfileVersionId = 0,
                ModelBindingId = binding.Id,
                ResultCode = (int)upstreamResponse.StatusCode,
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                TokensPerSecond = responseJson.Length / 4.0 / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds)
            };
            await harnessRunRepository.Add(run);
        }
        catch { }

        Response.StatusCode = (int)upstreamResponse.StatusCode;
        return Content(responseJson, "application/json");
    }

}
