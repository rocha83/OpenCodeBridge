using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>E2E de tools no modelo vivo (hermes, responses simples e com tools).</summary>
internal static class E2EToolTests
{
    public static async Task VllmToolHermes()
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
        var r = await TestContext.PostObj(TestContext.Vllm + "/v1/chat/completions", body);
        var tc = r["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
        TestContext.Check(tc is { Count: > 0 }, "hermes extraiu tool_call");
        TestContext.Check(tc![0]?["function"]?["name"]?.GetValue<string>() == "soma", "tool soma chamada");
    }

    public static async Task SimpleResponses()
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
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", body);
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "response completed");
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        TestContext.Check(output.Any(o => o?["type"]?.GetValue<string>() == "message"), "tem message");
        TestContext.Check(output.Any(o => o?["type"]?.GetValue<string>() == "reasoning"), "tem reasoning (events)");
    }

    public static async Task ToolResponses()
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
            ["tools"] = new JsonArray(TestContext.SomaToolResponses())
        };
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", body);
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "response completed");
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        TestContext.Check(output.Count > 0, "output nao vazio");
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
}
