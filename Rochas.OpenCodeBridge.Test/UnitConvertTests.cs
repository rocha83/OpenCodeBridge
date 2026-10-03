using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Unit de conversão completion→Responses (thinking, call_id, args inválidos).</summary>
internal static class UnitConvertTests
{
    public static async Task ConvertThinkingTools()
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
        var r = await TestContext.PostObj(TestContext.Bridge + "/api/convert", body);
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        var reasoning = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "reasoning");
        TestContext.Check(reasoning is not null, "thinking vira item reasoning");
        var msg = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "message")?.AsObject();
        string text = msg?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        TestContext.Check(!text.Contains("<think>"), "message limpa sem <think>");
        TestContext.Check(text.Contains("12"), "message tem conteudo");
        var call = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "function_call")?.AsObject();
        TestContext.Check(call?["name"]?.GetValue<string>() == "soma", "function_call preservada");
        TestContext.Check(call?["call_id"]?.GetValue<string>() == "call_1", "call_id preservado");
    }

    public static async Task ConvertInvalidArguments()
    {
        // Mitigação: arguments fora de JSON válido não devem quebrar o convert.
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["completion"] = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "vai somar",
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "call_bad", ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "soma", ["arguments"] = "{a:7,}" }
                        })
                    }
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 10, ["completion_tokens"] = 5 }
            }
        };
        try
        {
            var r = await TestContext.PostObj(TestContext.Bridge + "/api/convert", body);
            var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
            var call = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "function_call")?.AsObject();
            TestContext.Check(call is not null, "function_call preservada mesmo com args ruins");
        }
        catch (Exception ex) when (ex.Message.Contains("arguments"))
        {
            // Falha honesta com mensagem de arguments também é mitigação válida.
            TestContext.Check(true, "rejeição honesta de arguments");
        }
    }
}
