using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.AI.Manager.Application.Tools;

// Handler da tool "subagent"/"task": delega um prompt a um modelo (upstream)
// e executa o loop agéntico (tools reais) até obter a resposta final.
// Permite ao Build/Orch segmentar trabalho em sub-tarefas executadas de verdade.
public sealed class SubagentToolHandler : IToolHandler
{
    public string ToolName => "subagent";

    static readonly JsonArray ToolDefs = new()
    {
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="shell", ["description"]="executa comando shell", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["command"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("command") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="read", ["description"]="le arquivo", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="write", ["description"]="grava/cria arquivo", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["content"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","content") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="edit", ["description"]="substitui trecho exato", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["old"]=new JsonObject{["type"]="string"}, ["new"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","old","new") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="glob", ["description"]="lista arquivos por padrao", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["pattern"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("pattern") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="grep", ["description"]="busca regex", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["pattern"]=new JsonObject{["type"]="string"}, ["path"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("pattern") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="fetch", ["description"]="GET http", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["url"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("url") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="git", ["description"]="git somente-leitura", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["command"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("command") } } },
    };

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject args;
        try { args = JsonNode.Parse(argumentsJson)?.AsObject() ?? new JsonObject(); }
        catch { args = new JsonObject(); }

        string prompt = args["prompt"]?.GetValue<string>() ?? args["task"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("subagent: argumento 'prompt' obrigatorio");

        string system = args["system"]?.GetValue<string>() ?? "Voce e um agente desenvolvedor senior. Execute a tarefa usando as tools disponiveis e responda de forma concisa em pt-br.";
        string model = args["model"]?.GetValue<string>() ?? "";
        double temp = args["temperature"]?.GetValue<double>() ?? 0.4;

        string ws = args["workspace"]?.GetValue<string>() ?? context.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(ws)) ws = context.WorkspaceRoot;
        string upstream = string.IsNullOrWhiteSpace(context.UpstreamUrl) ? "http://127.0.0.1:4100" : context.UpstreamUrl;
        var executor = new BridgeToolExecutor(ws, upstream, context.ApiKey);

        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = prompt });

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(context.TimeoutSeconds, 30, 600)) };
            if (!string.IsNullOrWhiteSpace(context.ApiKey))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.ApiKey);

            for (int turn = 0; turn < 8; turn++)
            {
                var body = new JsonObject
                {
                    ["temperature"] = temp,
                    ["stream"] = false,
                    ["messages"] = messages.DeepClone(),
                    ["tools"] = ToolDefs.DeepClone()
                };
                if (!string.IsNullOrWhiteSpace(model)) body["model"] = model;

                var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                var resp = http.PostAsync(upstream + "/v1/chat/completions", content).GetAwaiter().GetResult();
                string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    return ToolResult.Fail($"subagent: upstream HTTP {(int)resp.StatusCode}: {raw[..Math.Min(300, raw.Length)]}");

                using var doc = JsonDocument.Parse(raw);
                var msgEl = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
                string text = msgEl.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
                JsonArray? calls = null;
                if (msgEl.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                {
                    calls = new JsonArray();
                    foreach (var e in tc.EnumerateArray())
                        calls.Add(JsonNode.Parse(e.GetRawText()));
                }

                if (calls is null || calls.Count == 0)
                    return ToolResult.Ok(text.Length > 0 ? text : "(sem resposta)");

                // echo assistant + execute tools
                var echo = new JsonArray();
                foreach (var tc1 in calls) echo.Add(new JsonObject { ["id"]=tc1?["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=tc1?["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=tc1?["function"]?["arguments"]?.GetValue<string>()??"{}" } });
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = text, ["tool_calls"] = echo });

                foreach (var tc2 in calls)
                {
                    string name = tc2?["function"]?["name"]?.GetValue<string>() ?? "";
                    string ajson = tc2?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                    var res = executor.Execute(name, ajson, context.TimeoutSeconds);
                    string outp = res.Success ? res.Output : res.Error;
                    if (outp.Length > 2000) outp = outp[..2000] + "...(truncado)";
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = tc2?["id"]?.GetValue<string>() ?? "", ["content"] = outp });
                }
            }
            return ToolResult.Ok("(loop esgotado)");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"subagent: {ex.Message}");
        }
    }
}
