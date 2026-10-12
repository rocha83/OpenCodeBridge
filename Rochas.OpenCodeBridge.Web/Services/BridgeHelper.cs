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
        double temperature, string systemPrompt, JsonArray messages, CancellationToken ct, int maxTokens = 8192, bool? enableThinking = null)
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
            var choices0 = chunk?["choices"]?.AsArray();
            var delta = choices0 is { Count: > 0 } ? choices0[0]?["delta"]?.AsObject() : null;
            // Usage vem em chunk próprio (choices vazio): ler antes do guard.
            var usage0 = chunk?["usage"]?.AsObject();
            if (usage0 is not null)
            {
                promptTokens = usage0["prompt_tokens"]?.GetValue<int>();
                completionTokens = usage0["completion_tokens"]?.GetValue<int>();
            }
            if (delta is null) continue;
            if (delta["reasoning_content"]?.GetValue<string>() is string r) thinking.Append(r);
            if (delta["content"]?.GetValue<string>() is string c) content.Append(c);
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
    // onChunk(kind thinking|content, texto): ao vivo, por delta (subagentes).
    public static async Task<ChatTurn> ChatTurnAsync(IBridgeClient bridge, string bridgeUrl, string model,
        double temperature, string systemPrompt, JsonArray messages, JsonArray? tools, CancellationToken ct, bool? enableThinking = null, int? maxTokens = null,
        Func<string, string, Task>? onChunk = null, JsonNode? toolChoice = null)
    {
        using var buffer = new MemoryStream();
        Stream output = buffer;
        ChunkTap? tap = null;
        if (onChunk is not null) { tap = new ChunkTap(buffer, onChunk); output = tap; }
        var (ok, error) = await bridge.StreamAsync(bridgeUrl, model, temperature, systemPrompt, messages, output, ct,
            includeTools: tools is not null, tools: tools, maxTokens: maxTokens, enableThinking: enableThinking,
            toolChoice: toolChoice);
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
            var choice = chunk?["choices"]?.AsArray() is { Count: > 0 } arr ? arr[0]?.AsObject() : null;
            if (choice is null)
            {
                // Chunk só de usage (choices vazio): captura e segue.
                var usageOnly = chunk?["usage"]?.AsObject();
                if (usageOnly is not null)
                {
                    promptTokens = usageOnly["prompt_tokens"]?.GetValue<int>();
                    completionTokens = usageOnly["completion_tokens"]?.GetValue<int>();
                }
                continue;
            }
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
        string txt = content.ToString();
        // Caminho DeepSeek: sem tool_calls estruturadas, o modelo escreve a
        // chamada no texto — coage para Calls (só quando tools foram pedidas;
        // texto puro de orquestração nunca é tocado).
        if (calls.Count == 0 && tools is not null && tools.Count > 0)
        {
            var coerced = DeepSeekToolCoercion.Coerce(model, txt, out string cleaned);
            if (coerced.Count > 0) { calls = coerced; txt = cleaned; }
        }
        return new ChatTurn(true, txt, thinking.ToString(), promptTokens, completionTokens, calls, "");
    }

    // Stream que repassa ao vivo os deltas (thinking/content) enquanto bufferiza.
    // Falha de parse nunca quebra o fluxo (best-effort).
    private sealed class ChunkTap : System.IO.Stream
    {
        private readonly System.IO.MemoryStream _inner;
        private readonly Func<string, string, Task> _onChunk;
        private string _rest = "";
        public ChunkTap(System.IO.MemoryStream inner, Func<string, string, Task> onChunk)
        { _inner = inner; _onChunk = onChunk; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(System.Threading.CancellationToken ct) => _inner.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, System.IO.SeekOrigin org) => throw new NotSupportedException();
        public override void SetLength(long v) => _inner.SetLength(v);
        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            try
            {
                _rest += System.Text.Encoding.UTF8.GetString(buffer, offset, count);
                int idx;
                while ((idx = _rest.IndexOf('\n')) >= 0)
                {
                    string line = _rest[..idx].TrimEnd('\r');
                    _rest = _rest[(idx + 1)..];
                    if (!line.StartsWith("data: ")) continue;
                    string data = line[6..].Trim();
                    if (data == "[DONE]") continue;
                    System.Text.Json.Nodes.JsonObject? chunk;
                    try { chunk = System.Text.Json.Nodes.JsonNode.Parse(data)?.AsObject(); }
                    catch { continue; }
                    var delta = chunk?["choices"]?[0]?["delta"]?.AsObject();
                    if (delta is null) continue;
                    if (delta["reasoning_content"]?.GetValue<string>() is string r && r.Length > 0)
                        try { _onChunk("thinking", r).GetAwaiter().GetResult(); } catch { }
                    if (delta["content"]?.GetValue<string>() is string c && c.Length > 0)
                        try { _onChunk("content", c).GetAwaiter().GetResult(); } catch { }
                }
                if (_rest.Length > 65536) _rest = _rest[^65536..];
            }
            catch { }
        }
    }
}
