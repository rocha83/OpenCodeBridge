using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Chamada não-streaming sobre qualquer IBridgeClient (real ou mock):
// executa o SSE num buffer e extrai texto/thinking/usage.
public static class BridgeHelper
{
    public sealed record ChatResult(bool Ok, string Content, string Thinking, int? PromptTokens, int? CompletionTokens, string Error);
    public sealed record ToolCall(string Id, string Name, string Args);
    public sealed record ChatTurn(bool Ok, string Content, string Thinking, int? PromptTokens, int? CompletionTokens, List<ToolCall> Calls, string Error);

    public static async Task<ChatResult> ChatAsync(IBridgeClient bridge, string bridgeUrl, string model,
        double temperature, string systemPrompt, JsonArray messages, CancellationToken ct, int maxTokens = 2048, bool? enableThinking = null)
    {
        using var buffer = new MemoryStream();
        // Orquestração é texto puro (sem tools): JSON de decomposição e artefatos
        // não podem se perder em tool_calls ignoradas.
        var (ok, error) = await bridge.StreamAsync(bridgeUrl, model, temperature, systemPrompt, messages, buffer, ct, includeTools: false, maxTokens: maxTokens, enableThinking: enableThinking);
        if (!ok) return new ChatResult(false, "", "", null, null, error);

        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        var content = new System.Text.StringBuilder();
        var thinking = new System.Text.StringBuilder();
        int? promptTokens = null, completionTokens = null;
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            string data = line[6..].Trim();
            if (data == "[DONE]") break;
            JsonObject? chunk;
            try { chunk = JsonNode.Parse(data)?.AsObject(); }
            catch { continue; }
            var delta = chunk?["choices"]?[0]?["delta"]?.AsObject();
            if (delta is null) continue;
            if (delta["reasoning_content"]?.GetValue<string>() is string r) thinking.Append(r);
            if (delta["content"]?.GetValue<string>() is string c) content.Append(c);
            var usage = chunk?["usage"]?.AsObject();
            if (usage is not null)
            {
                promptTokens = usage["prompt_tokens"]?.GetValue<int>();
                completionTokens = usage["completion_tokens"]?.GetValue<int>();
            }
        }
        // Mitigação do desvio reasoning/content (espelha a bridge /v1/responses:
        // "raciocínio vira texto comum" com thinking off): o parser do serve pode
        // despejar a resposta no canal reasoning quando o template não emite
        // <think>; sem isto o decompose enxergaria conteúdo vazio.
        string txt = content.ToString(), thk = thinking.ToString();
        if (enableThinking == false && string.IsNullOrWhiteSpace(txt) && !string.IsNullOrWhiteSpace(thk))
            txt = thk;
        return new ChatResult(true, txt, thk, promptTokens, completionTokens, "");
    }

    // Volta com tool_calls acumuladas (para loops de execução por subtarefa).
    public static async Task<ChatTurn> ChatTurnAsync(IBridgeClient bridge, string bridgeUrl, string model,
        double temperature, string systemPrompt, JsonArray messages, JsonArray? tools, CancellationToken ct, bool? enableThinking = null)
    {
        using var buffer = new MemoryStream();
        var (ok, error) = await bridge.StreamAsync(bridgeUrl, model, temperature, systemPrompt, messages, buffer, ct,
            includeTools: tools is not null, tools: tools, enableThinking: enableThinking);
        if (!ok) return new ChatTurn(false, "", "", null, null, new List<ToolCall>(), error);

        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        var content = new System.Text.StringBuilder();
        var thinking = new System.Text.StringBuilder();
        int? promptTokens = null, completionTokens = null;
        var accum = new Dictionary<int, (string Id, string Name, System.Text.StringBuilder Args)>();
        string? finishReason = null;
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            string data = line[6..].Trim();
            if (data == "[DONE]") break;
            JsonObject? chunk;
            try { chunk = JsonNode.Parse(data)?.AsObject(); }
            catch { continue; }
            var choice = chunk?["choices"]?[0]?.AsObject();
            if (choice is null) continue;
            finishReason = choice["finish_reason"]?.GetValue<string>() ?? finishReason;
            var delta = choice["delta"]?.AsObject();
            if (delta is null) continue;
            if (delta["reasoning_content"]?.GetValue<string>() is string r) thinking.Append(r);
            if (delta["content"]?.GetValue<string>() is string c) content.Append(c);
            if (delta["tool_calls"]?.AsArray() is JsonArray tcArr)
            {
                foreach (var tc in tcArr)
                {
                    var tcObj = tc?.AsObject();
                    if (tcObj is null) continue;
                    int index = tcObj["index"]?.GetValue<int>() ?? 0;
                    string id = tcObj["id"]?.GetValue<string>() ?? "";
                    var fn = tcObj["function"]?.AsObject();
                    string name = fn?["name"]?.GetValue<string>() ?? "";
                    string args = fn?["arguments"]?.GetValue<string>() ?? "";
                    if (!accum.TryGetValue(index, out var existing))
                    {
                        existing = (id, name, new System.Text.StringBuilder());
                        accum[index] = existing;
                    }
                    if (!string.IsNullOrEmpty(id)) existing.Id = id;
                    if (!string.IsNullOrEmpty(name)) existing.Name = name;
                    existing.Args.Append(args);
                }
            }
            var usage = chunk?["usage"]?.AsObject();
            if (usage is not null)
            {
                promptTokens = usage["prompt_tokens"]?.GetValue<int>();
                completionTokens = usage["completion_tokens"]?.GetValue<int>();
            }
        }
        var calls = finishReason == "tool_calls"
            ? accum.Values
                .Where(t => !string.IsNullOrEmpty(t.Id) && !string.IsNullOrEmpty(t.Name))
                .Select(t => new ToolCall(t.Id, t.Name, t.Args.ToString()))
                .ToList()
            : new List<ToolCall>();
        return new ChatTurn(true, content.ToString(), thinking.ToString(), promptTokens, completionTokens, calls, "");
    }
}
