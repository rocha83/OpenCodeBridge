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
        if (TestContext.WantsReasoning) TestContext.Check(reasoning is not null, "thinking vira item reasoning");
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

    public static async Task ConvertCoercedTextCall()
    {
        // Linha llama/CPU: texto com fenced call vira function_call quando o
        // model id contém "cpu". Caminho vLLM (hermes & cia) jamais entra aqui.
        var body = new JsonObject
        {
            ["model"] = "openai/qwen-cpu-3b",
            ["completion"] = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "```json\n{\"name\": \"get_status\", \"arguments\": {\"path\": \"/tmp\"}}\n```"
                    }
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 20, ["completion_tokens"] = 15 }
            }
        };
        var r = await TestContext.PostObj(TestContext.Bridge + "/api/convert", body);
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        var call = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "function_call")?.AsObject()
            ?? throw new Exception("sem function_call coagida");
        TestContext.Check(call?["name"]?.GetValue<string>() == "get_status", "nome extraido");
        TestContext.Check((call?["arguments"]?.GetValue<string>() ?? "").Contains("/tmp"), "args extraidos");
    }

    public static async Task ConvertCoercedAngleCall()
    {
        // Formato cru do 3B na CPU (observado ao vivo): <{"name": ..}}> sem
        // fences nem tags — tem que coagir igual.
        var body = new JsonObject
        {
            ["model"] = "llama/qwen25-coder-3b-cpu-build",
            ["completion"] = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "<{\"name\": \"soma\", \"arguments\": {\"a\": 17, \"b\": 25}}}>\" 42"
                    }
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 20, ["completion_tokens"] = 15 }
            }
        };
        var r = await TestContext.PostObj(TestContext.Bridge + "/api/convert", body);
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        var call = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "function_call")?.AsObject()
            ?? throw new Exception("sem function_call coagida (angle)");
        TestContext.Check(call?["name"]?.GetValue<string>() == "soma", "nome extraido (angle)");
        TestContext.Check((call?["arguments"]?.GetValue<string>() ?? "").Contains("17"), "args extraidos (angle)");

        // Objeto cru no inicio (outro formato do 3B entre runs).
        var bare = new JsonObject
        {
            ["model"] = "llama/qwen25-coder-3b-cpu-build",
            ["completion"] = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = "{\"name\": \"soma\", \"arguments\": {\"a\": 1, \"b\": 2}}\n[END_OF_TEXT]"
                    }
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 20, ["completion_tokens"] = 15 }
            }
        };
        var rb = await TestContext.PostObj(TestContext.Bridge + "/api/convert", bare);
        var outb = rb["output"]?.AsArray() ?? throw new Exception("sem output (bare)");
        var callb = outb.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "function_call")?.AsObject()
            ?? throw new Exception("sem function_call coagida (bare)");
        TestContext.Check(callb?["name"]?.GetValue<string>() == "soma", "nome extraido (bare)");
    }
}
