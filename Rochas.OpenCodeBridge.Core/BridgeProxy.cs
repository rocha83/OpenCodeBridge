using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Proxy in-process para o llama.cpp: recebe o corpo já montado pelo cliente
/// (chat ou responses), injeta os parâmetros da instância (model, temperatura,
/// teto, thinking e caudas) e devolve a resposta HTTP do upstream para o
/// chamador consumir como quiser (stream ou não).
/// </summary>
public static class BridgeProxy
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Envia /v1/chat/completions ao llama com as opções da instância.</summary>
    /// <param name="options">Configuração da instância lógica.</param>
    /// <param name="body">Corpo original do cliente (não é mutado).</param>
    /// <param name="stream">true = SSE incremental.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Resposta do upstream (o chamador descarta).</returns>
    public static Task<HttpResponseMessage> SendChatAsync(BridgeOptions options, JsonObject body, bool stream, CancellationToken ct)
    {
        var clone = Clone(body);
        clone["model"] = options.Model;
        if (clone["temperature"] is null) clone["temperature"] = options.Temperature;
        if (options.MaxTokens > 0 && clone["max_tokens"] is null && clone["max_completion_tokens"] is null)
            clone["max_tokens"] = options.MaxTokens;
        if (options.EnableThinking.HasValue)
            clone["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = options.EnableThinking.Value };
        clone["stream"] = stream;
        ApplyTailsToChat(clone, options);
        return PostAsync(options, "/v1/chat/completions", clone, ct);
    }

    /// <summary>Envia /v1/responses ao llama (suportado nativamente) com as caudas.</summary>
    /// <param name="options">Configuração da instância lógica.</param>
    /// <param name="body">Corpo original do cliente (não é mutado).</param>
    /// <param name="stream">true = SSE incremental.</param>
    /// <param name="ct">Cancelamento.</param>
    /// <returns>Resposta do upstream (o chamador descarta).</returns>
    public static Task<HttpResponseMessage> SendResponsesAsync(BridgeOptions options, JsonObject body, bool stream, CancellationToken ct)
    {
        var clone = Clone(body);
        clone["model"] = options.Model;
        if (clone["temperature"] is null) clone["temperature"] = options.Temperature;
        if (options.EnableThinking.HasValue)
            clone["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = options.EnableThinking.Value };
        string instructions = clone["instructions"]?.GetValue<string>() ?? "";
        string composed = HarnessTails.Compose(instructions, options.Harness, options.HarnessBehaviorTail, options.HarnessToolsTail);
        if (!string.IsNullOrWhiteSpace(composed)) clone["instructions"] = composed;
        else clone.Remove("instructions");
        clone["stream"] = stream;
        return PostAsync(options, "/v1/responses", clone, ct);
    }

    /// <summary>GET /health do llama (usado pelo Ping da Web UI).</summary>
    public static async Task<bool> PingAsync(BridgeOptions options, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, options.UpstreamUrl.TrimEnd('/') + "/health");
            Authorize(req, options.ApiKey);
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return res.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<HttpResponseMessage> PostAsync(BridgeOptions options, string path, JsonObject body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, options.UpstreamUrl.TrimEnd('/') + path)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(req, options.ApiKey);
        return await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static void Authorize(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    internal static JsonObject Clone(JsonObject body)
        => JsonNode.Parse(body.ToJsonString())?.AsObject() ?? new JsonObject();

    private static void ApplyTailsToChat(JsonObject body, BridgeOptions options)
    {
        if (!options.Harness) return;
        if (body["messages"] is not JsonArray messages) return;
        foreach (var node in messages)
        {
            if (node is not JsonObject msg) continue;
            string? role = msg["role"]?.GetValue<string>();
            if (role is not ("system" or "developer")) continue;
            if (msg["content"] is JsonValue value && value.TryGetValue<string>(out string? text))
            {
                msg["content"] = HarnessTails.Compose(text, true, options.HarnessBehaviorTail, options.HarnessToolsTail);
                return;
            }
            if (msg["content"] is JsonArray parts)
            {
                foreach (var part in parts)
                {
                    if (part is JsonObject po && po["text"] is JsonValue pv && pv.TryGetValue<string>(out string? ptext))
                    {
                        po["text"] = HarnessTails.Compose(ptext, true, options.HarnessBehaviorTail, options.HarnessToolsTail);
                        return;
                    }
                }
            }
        }
    }
}
