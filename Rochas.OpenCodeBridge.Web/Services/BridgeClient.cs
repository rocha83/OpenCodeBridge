using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Agente no banco tem seu próprio SystemPrompt; a bridge é agnóstica.
// Proxy p/ bridge: monta chat completions e repassa o stream SSE cru.
public sealed class BridgeClient(IHttpClientFactory http) : IBridgeClient
{
    public async Task<(bool ok, string error)> StreamAsync(string bridgeUrl, string model, double temperature,
        string systemPrompt, JsonArray messages, Stream output, CancellationToken ct)
    {
        try
        {
            var all = new JsonArray();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                all.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });
            foreach (var m in messages) all.Add(m?.DeepClone());

            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = all,
                ["temperature"] = temperature,
                ["max_tokens"] = 2048,
                ["stream"] = true,
                ["tools"] = ToolDefinitions.GetTools(),
                ["tool_choice"] = "auto"
            };
            var client = http.CreateClient();
            client.Timeout = Timeout.InfiniteTimeSpan;
            using var res = await client.PostAsync(bridgeUrl.TrimEnd('/') + "/v1/chat/completions",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            if (!res.IsSuccessStatusCode)
                return (false, $"bridge {(int)res.StatusCode}");
            using var stream = await res.Content.ReadAsStreamAsync(ct);
            await stream.CopyToAsync(output, ct);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}