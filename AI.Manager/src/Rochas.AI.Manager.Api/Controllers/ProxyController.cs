using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Route("v1")]
public sealed class ProxyController : ControllerBase
{
    private readonly IHttpClientFactory _http;
    private readonly IModelBindingService _bindings;
    public ProxyController(IHttpClientFactory http, IModelBindingService bindings) { _http = http; _bindings = bindings; }

    [HttpPost("chat/completions")]
    public async Task<IActionResult> ChatCompletions([FromBody] object body, CancellationToken ct)
    {
        // Default to first binding; model selection via body.model (provider/model) is resolved by bridge in practice.
        var list = await _bindings.ListAsync(ct);
        var binding = list.FirstOrDefault();
        if (binding is null) return Problem("No model binding configured.");
        var baseUrl = binding.Provider.Equals("llama", StringComparison.OrdinalIgnoreCase) ? "http://localhost:4125" : "http://localhost:4124";
        var url = $"{baseUrl}/v1/chat/completions";
        var client = _http.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "guest@123");
        var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        Response.StatusCode = (int)resp.StatusCode;
        foreach (var h in resp.Headers) Response.Headers[h.Key] = h.Value.ToArray();
        Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
        await stream.CopyToAsync(Response.Body, ct);
        return new EmptyResult();
    }
}