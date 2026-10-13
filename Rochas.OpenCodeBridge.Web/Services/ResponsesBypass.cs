using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Caminho bypass da UI web (flag Orchestration:UseResponsesBypass, padrao ligado):
// turno unico contra {bridge}/v1/responses, SEM ToolExecutor. O sanitize leve
// (NormalizeSystemFirst + StripReasoningItems) mora na bridge com passthrough;
// aqui só traduzimos o SSE Responses -> chat SSE que a UI já entende.
public static class ResponsesBypass
{
    // Monta o corpo Responses: instructions separadas + input de mensagens
    // {type: message, role, content} (a bridge funde system/developer no inicial).
    // Tools nativas (Responses flat): o modelo devolve function_call; QUEM executa
    // é o chamador (opencode executa as dele, o site executa via ToolExecutor).
    public static JsonObject BuildBody(string model, double temperature, string instructions,
        JsonArray messages, int? maxTokens = null, JsonArray? tools = null)
    {
        var input = ToInputItems(messages);
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = input,
            ["stream"] = true,
            ["temperature"] = temperature,
        };
        if (!string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions;
        if (maxTokens.HasValue) body["max_tokens"] = maxTokens.Value;
        if (tools is { Count: > 0 }) body["tools"] = tools.DeepClone();
        return body;
    }

    // Mensagens chat -> itens Responses (reuso no loop: outputs voltam aqui).
    public static JsonArray ToInputItems(JsonArray messages)
    {
        var input = new JsonArray();
        foreach (var m in messages)
        {
            var o = m?.AsObject();
            if (o is null) continue;
            string role = o["role"]?.GetValue<string>() ?? "user";
            if (role is not ("user" or "assistant" or "system" or "developer")) role = "user";
            string text = o["content"]?.GetValue<string>() ?? "";
            input.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = role,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
            });
        }
        return input;
    }

    // Chat-style {type: function, function: {name, description, parameters}} ->
    // Responses flat {type: function, name, description, parameters} (a bridge
    // aceita os dois; flat é o nativo).
    public static JsonArray ToResponsesTools(JsonArray chatTools)
    {
        var out_ = new JsonArray();
        foreach (var t in chatTools)
        {
            var o = t?.AsObject();
            if (o is null) continue;
            var fn = o["function"]?.AsObject();
            if (fn is null) continue;
            string name = fn["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name)) continue;
            out_.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = name,
                ["description"] = fn["description"]?.GetValue<string>() ?? "",
                ["parameters"] = fn["parameters"]?.DeepClone() ?? new JsonObject(),
            });
        }
        return out_;
    }

    // Acumulador de function_call no SSE (delta -> done). Puro e testável.
    public sealed class CallAccum
    {
        private sealed class State
        {
            public string CallId = "";
            public string Name = "";
            public readonly StringBuilder Args = new();
            public bool Done;
        }
        private readonly Dictionary<string, State> _byItem = new();
        public readonly List<(string Id, string Name, string Args)> Calls = new();

        public void Feed(JsonObject evt)
        {
            string type = evt["type"]?.GetValue<string>() ?? "";
            if (type == "response.output_item.added" && evt["item"]?.AsObject() is JsonObject item
                && item["type"]?.GetValue<string>() == "function_call")
            {
                string id = item["id"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(id)) return;
                var st = new State
                {
                    CallId = item["call_id"]?.GetValue<string>() ?? id,
                    Name = item["name"]?.GetValue<string>() ?? "",
                };
                string a = item["arguments"]?.GetValue<string>() ?? "";
                if (a.Length > 0) st.Args.Append(a);
                _byItem[id] = st;
            }
            else if (type == "response.function_call_arguments.delta")
            {
                string id = evt["item_id"]?.GetValue<string>() ?? "";
                if (id.Length > 0 && evt["delta"]?.GetValue<string>() is string d)
                {
                    if (!_byItem.TryGetValue(id, out var st)) { st = new State(); _byItem[id] = st; }
                    st.Args.Append(d);
                }
            }
            else if (type is "response.function_call_arguments.done" or "response.output_item.done")
            {
                string id = evt["item_id"]?.GetValue<string>() ?? "";
                JsonObject? doneItem = evt["item"]?.AsObject();
                if (id.Length == 0 && doneItem is not null && doneItem["type"]?.GetValue<string>() == "function_call")
                    id = doneItem["id"]?.GetValue<string>() ?? "";
                if (id.Length > 0 && _byItem.TryGetValue(id, out var st) && !st.Done)
                {
                    if (doneItem is not null)
                    {
                        if (string.IsNullOrEmpty(st.Name)) st.Name = doneItem["name"]?.GetValue<string>() ?? "";
                        if (string.IsNullOrEmpty(st.CallId)) st.CallId = doneItem["call_id"]?.GetValue<string>() ?? id;
                        if (st.Args.Length == 0) st.Args.Append(doneItem["arguments"]?.GetValue<string>() ?? "");
                    }
                    string args = evt["arguments"]?.GetValue<string>() ?? st.Args.ToString();
                    st.Done = true;
                    if (!string.IsNullOrEmpty(st.Name)) Calls.Add((st.CallId.Length > 0 ? st.CallId : id, st.Name, args));
                }
            }
        }
    }

    public sealed record TranslatedLine(string? ChatData, bool Completed, string? Error);

    // Traduz UMA linha "data:" do SSE Responses. Acumula texto/thinking/usage
    // nos builders; devolve a linha chat SSE equivalente p/ repasse ao vivo
    // (null = nada a repassar). Puro e testável (sem HTTP). Com accum, coleta
    // function_call p/ o loop executar via ToolExecutor e reenviar outputs.
    public static TranslatedLine TranslateData(string data, StringBuilder content, StringBuilder thinking,
        ref int? promptTokens, ref int? completionTokens, CallAccum? accum = null)
    {
        if (data == "[DONE]") return new TranslatedLine("[DONE]", true, null);
        JsonObject? evt;
        try { evt = JsonNode.Parse(data)?.AsObject(); }
        catch { return new TranslatedLine(null, false, null); }
        if (evt is null) return new TranslatedLine(null, false, null);
        string type = evt["type"]?.GetValue<string>() ?? "";

        if (type == "response.output_text.delta" && evt["delta"]?.GetValue<string>() is string d && d.Length > 0)
        {
            content.Append(d);
            var chat = new JsonObject
            {
                ["choices"] = new JsonArray { new JsonObject { ["delta"] = new JsonObject { ["content"] = d } } },
            };
            return new TranslatedLine(chat.ToJsonString(), false, null);
        }
        if ((type == "response.reasoning_summary_text.delta" || type == "response.reasoning_text.delta")
            && evt["delta"]?.GetValue<string>() is string r && r.Length > 0)
        {
            thinking.Append(r);
            var chat = new JsonObject
            {
                ["choices"] = new JsonArray { new JsonObject { ["delta"] = new JsonObject { ["reasoning_content"] = r } } },
            };
            return new TranslatedLine(chat.ToJsonString(), false, null);
        }
        if (type == "response.completed")
        {
            var usage = evt["response"]?["usage"]?.AsObject();
            if (usage is not null)
            {
                promptTokens = usage["input_tokens"]?.GetValue<int>() ?? promptTokens;
                completionTokens = usage["output_tokens"]?.GetValue<int>() ?? completionTokens;
            }
            var chat = new JsonObject
            {
                ["choices"] = new JsonArray { new JsonObject { ["finish_reason"] = "stop" } },
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = promptTokens ?? 0,
                    ["completion_tokens"] = completionTokens ?? 0,
                },
            };
            return new TranslatedLine(chat.ToJsonString(), true, null);
        }
        if (type is "response.failed" or "error")
        {
            string msg = evt["error"]?["message"]?.GetValue<string>()
                ?? evt["response"]?["error"]?.GetValue<string>()
                ?? "falha no bypass";
            return new TranslatedLine(null, true, msg);
        }
        if (type is "response.output_item.added" or "response.function_call_arguments.delta"
            or "response.function_call_arguments.done" or "response.output_item.done")
            accum?.Feed(evt);
        return new TranslatedLine(null, false, null);
    }
}
