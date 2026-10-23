using System.Text;
using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Web.Services;

// Agente no banco tem seu próprio SystemPrompt; a bridge é agnóstica.
// Cliente in-process: monta o chat completions e chama o Core (BridgeProxy)
// direto no llama — sem porta de bridge e sem args de processo.
public sealed class BridgeClient(Microsoft.Extensions.Configuration.IConfiguration cfg) : IBridgeClient
{
    public async Task<(bool ok, string error)> StreamAsync(string bridgeUrl, string model, double temperature,
        string systemPrompt, JsonArray messages, Stream output, CancellationToken ct,
        bool includeTools = true, JsonArray? tools = null, int? maxTokens = null, bool? enableThinking = null,
        JsonNode? toolChoice = null)
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
                ["stream"] = true,
                // Trava anti-eco degenerado ([shell] ok x500): repete menos.
                ["repetition_penalty"] = 1.15,
                // Sem isto o llama não devolve usage no stream (donut no chute).
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
            };
            if (maxTokens.HasValue) body["max_tokens"] = maxTokens.Value;
            if (includeTools)
            {
                body["tools"] = (JsonNode?)tools?.DeepClone() ?? ToolDefinitions.GetTools();
                body["tool_choice"] = toolChoice ?? JsonValue.Create("auto");
            }
            // Corta o mimetismo  thinking na fonte (executores non-thinking).
            if (enableThinking.HasValue)
                body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = enableThinking.Value };

            var options = new BridgeOptions
            {
                UpstreamUrl = bridgeUrl,
                Model = model,
                ApiKey = cfg["Llama:ApiKey"] ?? "guest@123",
                Temperature = temperature,
                MaxTokens = maxTokens ?? 16384,
                EnableThinking = enableThinking,
                Harness = true,
                LegacyHarness = false,
                CoerceTextTools = false,
                CoerceNarrated = false,
                ToolStrategy = false,
            };
            using var res = await BridgeProxy.SendChatAsync(options, body, stream: true, ct);
            if (!res.IsSuccessStatusCode)
            {
                string errBody = "";
                try { errBody = await res.Content.ReadAsStringAsync(ct); } catch { }
                if (errBody.Length > 400) errBody = errBody[..400];
                return (false, $"llama {(int)res.StatusCode}: {errBody}");
            }
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
