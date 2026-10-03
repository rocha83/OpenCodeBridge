// ===========================================================================
//  Rochas.OpenCodeBridge.Test — suite unit + e2e da bridge (BCL only, sem NuGet)
//  Runner console: exit 0 = tudo PASS, 1 = algum FAIL.
//  Uso: dotnet run -c Release -- [--bridge URL] [--vllm URL] [--skip-e2e]
//  Unit = traduz/converte via HTTP na bridge (sem modelo). E2E = modelo vivo.
// ===========================================================================

using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

internal static class Program
{
    static string Bridge = "http://127.0.0.1:4124";
    static string Vllm = "http://127.0.0.1:4100";
    static bool SkipE2E;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static int Passed;
    static int Failed;

    static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bridge" && i + 1 < args.Length) Bridge = args[++i].TrimEnd('/');
            else if (args[i] == "--vllm" && i + 1 < args.Length) Vllm = args[++i].TrimEnd('/');
            else if (args[i] == "--skip-e2e") SkipE2E = true;
        }
        Console.WriteLine($"[test] bridge={Bridge} vllm={Vllm} skipE2E={SkipE2E}");

        // ---- unit (bridge, sem modelo)
        Run("U-status", UStatus);
        Run("U-metrics", UMetrics);
        Run("U-models-passthrough", UModelsPassthrough);
        Run("U-translate-tools", UTranslateTools);
        Run("U-translate-sem-stop", UTranslateSemStop);
        Run("U-convert-thinking-tools", UConvertThinkingTools);

        // ---- e2e (modelo vivo)
        if (!SkipE2E)
        {
            Run("E-vllm-tool-hermes", EVllmToolHermes);
            Run("E-responses-simples", EResponsesSimples);
            Run("E-responses-tools", EResponsesTools);
            Run("E-stream-arquiteto", EStreamArquiteto);
            Run("E-review-arquiteto", EReviewArquiteto);
        }

        Console.WriteLine($"[test] PASS={Passed} FAIL={Failed}");
        return Failed == 0 ? 0 : 1;
    }

    static void Run(string name, Func<Task> fn)
    {
        var t0 = DateTime.UtcNow;
        try
        {
            fn().GetAwaiter().GetResult();
            Passed++;
            Console.WriteLine($"PASS {name} ({(DateTime.UtcNow - t0).TotalSeconds:F1}s)");
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    static void Check(bool cond, string msg)
    {
        if (!cond) throw new Exception("assert: " + msg);
    }

    static async Task<JsonObject> GetObj(string url)
    {
        using var res = await Http.GetAsync(url);
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"GET {url} -> {(int)res.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        return JsonNode.Parse(body)?.AsObject() ?? throw new Exception("GET resposta nao-JSON");
    }

    static async Task<JsonObject> PostObj(string url, JsonNode body)
    {
        using var res = await Http.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"POST {url} -> {(int)res.StatusCode}: {text[..Math.Min(300, text.Length)]}");
        return JsonNode.Parse(text)?.AsObject() ?? throw new Exception("POST resposta nao-JSON");
    }

    // ---- unit -----------------------------------------------------------

    static async Task UStatus()
    {
        var s = await GetObj(Bridge + "/api/status");
        Check(s["status"]?.GetValue<string>() == "ok", "status ok");
        Check(s["model"]?.GetValue<string>() == "qwen3-8b-awq", "model qwen3-8b-awq");
        Check(s["upstream"]?.GetValue<string>() == Vllm, "upstream == vllm");
    }

    static async Task UMetrics()
    {
        var m = await GetObj(Bridge + "/api/metrics");
        Check(m["requests"] is not null, "requests presente");
        Check(m["tps_output"] is not null && m["tps_total"] is not null, "tps presentes");
    }

    static async Task UModelsPassthrough()
    {
        var models = await GetObj(Bridge + "/v1/models");
        var first = models["data"]?[0]?["id"]?.GetValue<string>();
        Check(first == "qwen3-8b-awq", "passthrough lista qwen3-8b-awq, veio: " + first);
    }

    static JsonObject SomaToolResponses() => new()
    {
        ["type"] = "function",
        ["name"] = "soma",
        ["description"] = "Soma dois inteiros",
        ["parameters"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["a"] = new JsonObject { ["type"] = "integer" },
                ["b"] = new JsonObject { ["type"] = "integer" }
            },
            ["required"] = new JsonArray("a", "b")
        }
    };

    static async Task UTranslateTools()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["instructions"] = "Voce e um arquiteto de software senior. Respostas curtas.",
            ["input"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "message", ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Quanto e 7 + 5?" })
                },
                new JsonObject
                {
                    ["type"] = "function_call", ["id"] = "call_hist1", ["call_id"] = "call_hist1",
                    ["name"] = "soma", ["arguments"] = "{\"a\":7,\"b\":5}"
                },
                new JsonObject
                {
                    ["type"] = "function_call_output", ["call_id"] = "call_hist1", ["output"] = "12"
                }),
            ["tools"] = new JsonArray(
                SomaToolResponses(),
                new JsonObject
                {
                    ["type"] = "namespace", ["name"] = "fs",
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "read_file", ["description"] = "Le arquivo",
                        ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                    })
                })
        };
        var tr = await PostObj(Bridge + "/api/translate", body);
        var chat = tr["chat_request"]?.AsObject() ?? throw new Exception("sem chat_request");
        Check(chat["model"]?.GetValue<string>() == "qwen3-8b-awq", "upstream forca model servido");
        Check(chat["tool_choice"]?.GetValue<string>() == "auto", "tool_choice auto");
        var msgs = chat["messages"]?.AsArray() ?? throw new Exception("sem messages");
        Check(msgs.Any(m => m?["role"]?.GetValue<string>() == "system"), "instructions vira system");
        Check(msgs.Any(m => m?["role"]?.GetValue<string>() == "tool" && m?["tool_call_id"]?.GetValue<string>() == "call_hist1"), "tool preserva call_id");
        var tools = chat["tools"]?.AsArray() ?? throw new Exception("sem tools");
        Check(tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == "soma"), "function flat convertida");
        Check(tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == "read_file"), "namespace achatada");
        Check(tr["tool_count"]?.GetValue<int>() == 2, "tool_count 2, veio: " + tr["tool_count"]);
    }

    static async Task UTranslateSemStop()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "oi" })
            }),
            ["tools"] = new JsonArray(SomaToolResponses())
        };
        var tr = await PostObj(Bridge + "/api/translate", body);
        var chat = tr["chat_request"]?.AsObject() ?? throw new Exception("sem chat_request");
        Check(chat["stop"] is null, "stop nao repassado com tools (truncaria tool call)");
    }

    static async Task UConvertThinkingTools()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["temperature"] = 0.2,
            ["completion"] = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["reasoning_content"] = "vou somar via tool",
                        ["content"] = "<think>confere 7+5</think>O resultado e 12.",
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "call_1", ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "soma", ["arguments"] = "{\"a\":7,\"b\":5}" }
                        })
                    }
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 50, ["completion_tokens"] = 30 }
            }
        };
        var r = await PostObj(Bridge + "/api/convert", body);
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        var reasoning = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "reasoning");
        Check(reasoning is not null, "thinking vira item reasoning");
        var msg = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "message")?.AsObject();
        string text = msg?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Check(!text.Contains("<think>"), "message limpa sem <think>");
        Check(text.Contains("12"), "message tem conteudo");
        var call = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "function_call")?.AsObject();
        Check(call?["name"]?.GetValue<string>() == "soma", "function_call preservada");
        Check(call?["call_id"]?.GetValue<string>() == "call_1", "call_id preservado");
    }

    // ---- e2e (modelo vivo) -----------------------------------------------

    static async Task EVllmToolHermes()
    {
        var body = new JsonObject
        {
            ["model"] = "qwen3-8b-awq",
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Quanto e 7 + 5? Use a ferramenta soma." }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "soma", ["description"] = "Soma dois inteiros",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["a"] = new JsonObject { ["type"] = "integer" },
                            ["b"] = new JsonObject { ["type"] = "integer" }
                        },
                        ["required"] = new JsonArray("a", "b")
                    }
                }
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "soma" } },
            ["max_tokens"] = 256,
            ["temperature"] = 0.1
        };
        var r = await PostObj(Vllm + "/v1/chat/completions", body);
        var tc = r["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
        Check(tc is { Count: > 0 }, "hermes extraiu tool_call");
        Check(tc![0]?["function"]?["name"]?.GetValue<string>() == "soma", "tool soma chamada");
    }

    static async Task EResponsesSimples()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["stream"] = false,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Responda exatamente: ponte-ok" })
            })
        };
        var r = await PostObj(Bridge + "/v1/responses", body);
        Check(r["status"]?.GetValue<string>() == "completed", "response completed");
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        Check(output.Any(o => o?["type"]?.GetValue<string>() == "message"), "tem message");
        Check(output.Any(o => o?["type"]?.GetValue<string>() == "reasoning"), "tem reasoning (events)");
    }

    static async Task EResponsesTools()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["stream"] = false,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Quanto e 7 + 5? Use a ferramenta soma e so entao responda." })
            }),
            ["tools"] = new JsonArray(SomaToolResponses())
        };
        var r = await PostObj(Bridge + "/v1/responses", body);
        Check(r["status"]?.GetValue<string>() == "completed", "response completed");
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        Check(output.Count > 0, "output nao vazio");
        foreach (var item in output)
        {
            if (item?["type"]?.GetValue<string>() == "function_call")
            {
                string args = item?["arguments"]?.GetValue<string>() ?? "";
                try { JsonNode.Parse(args); }
                catch { throw new Exception("arguments invalido: " + args); }
            }
        }
        Console.WriteLine($"  [info] tool chamada: {output.Any(o => o?["type"]?.GetValue<string>() == "function_call")}");
    }

    static async Task EStreamArquiteto()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["stream"] = true,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Como arquiteto senior: 1 frase sobre quando usar fila em vez de chamada direta." })
            })
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, Bridge + "/v1/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"stream -> {(int)res.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        Check((res.Content.Headers.ContentType?.MediaType ?? "") == "text/event-stream", "content-type sse");
        string? curEvent = null, lastType = null;
        int lastSeq = -1, events = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("event:")) curEvent = line[6..].Trim();
            else if (line.StartsWith("data:") && curEvent is not null)
            {
                var payload = JsonNode.Parse(line[5..].Trim())?.AsObject();
                int seq = payload?["sequence_number"]?.GetValue<int>() ?? -1;
                Check(seq >= lastSeq, $"sequence monotônico ({lastSeq}->{seq})");
                lastSeq = seq; lastType = curEvent; events++; curEvent = null;
                if (lastType == "response.completed") break;
            }
        }
        Check(events > 3, "eventos suficientes");
        Check(lastType == "response.completed", "termina em response.completed");
    }

    static async Task EReviewArquiteto()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["stream"] = false,
            ["instructions"] = "Voce e um arquiteto de software senior fazendo code review. Seja direto: 1 risco principal.",
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = "Revise este diff:\n```diff\n- var logDir = Path.GetDirectoryName(LogPath);\n+ var logDir = Path.GetDirectoryName(LogPath)!;\n```\nQual o risco?"
                })
            }),
            ["tools"] = new JsonArray(SomaToolResponses())
        };
        var r = await PostObj(Bridge + "/v1/responses", body);
        Check(r["status"]?.GetValue<string>() == "completed", "review completed");
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        Check(output.Any(o => o?["type"]?.GetValue<string>() == "reasoning"), "review tem reasoning");
        var msg = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "message")?.AsObject();
        string text = msg?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        Check(text.Length > 20, "review tem texto util");
    }
}
