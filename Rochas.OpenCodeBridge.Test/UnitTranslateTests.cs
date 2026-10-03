using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Unit de tradução Responses→chat (tools, namespaces, stop suprimido).</summary>
internal static class UnitTranslateTests
{
    public static async Task TranslateTools()
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
                TestContext.SomaToolResponses(),
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
        var tr = await TestContext.PostObj(TestContext.Bridge + "/api/translate", body);
        var chat = tr["chat_request"]?.AsObject() ?? throw new Exception("sem chat_request");
        TestContext.Check(chat["model"]?.GetValue<string>() == "qwen3-8b-awq", "upstream forca model servido");
        TestContext.Check(chat["tool_choice"]?.GetValue<string>() == "auto", "tool_choice auto");
        var msgs = chat["messages"]?.AsArray() ?? throw new Exception("sem messages");
        TestContext.Check(msgs.Any(m => m?["role"]?.GetValue<string>() == "system"), "instructions vira system");
        TestContext.Check(msgs.Any(m => m?["role"]?.GetValue<string>() == "tool" && m?["tool_call_id"]?.GetValue<string>() == "call_hist1"), "tool preserva call_id");
        var tools = chat["tools"]?.AsArray() ?? throw new Exception("sem tools");
        TestContext.Check(tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == "soma"), "function flat convertida");
        TestContext.Check(tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == "read_file"), "namespace achatada");
        TestContext.Check(tr["tool_count"]?.GetValue<int>() == 2, "tool_count 2, veio: " + tr["tool_count"]);
    }

    public static async Task TranslateWithoutStop()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "oi" })
            }),
            ["tools"] = new JsonArray(TestContext.SomaToolResponses())
        };
        var tr = await TestContext.PostObj(TestContext.Bridge + "/api/translate", body);
        var chat = tr["chat_request"]?.AsObject() ?? throw new Exception("sem chat_request");
        TestContext.Check(chat["stop"] is null, "stop nao repassado com tools (truncaria tool call)");
    }

    public static async Task TranslateMultiNamespace()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "lê arquivo e soma" })
            }),
            ["tools"] = new JsonArray(
                TestContext.SomaToolResponses(),
                new JsonObject
                {
                    ["type"] = "namespace", ["name"] = "iot",
                    ["tools"] = new JsonArray(
                        new JsonObject { ["name"] = "read_sensor", ["description"] = "Lê sensor IoT", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["device"] = new JsonObject { ["type"] = "string" } } } },
                        new JsonObject { ["name"] = "publish_event", ["description"] = "Publica na fila", ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["topic"] = new JsonObject { ["type"] = "string" } } } })
                })
        };
        var tr = await TestContext.PostObj(TestContext.Bridge + "/api/translate", body);
        var chat = tr["chat_request"]?.AsObject() ?? throw new Exception("sem chat_request");
        TestContext.Check(tr["tool_count"]?.GetValue<int>() == 3, "tool_count 3, veio: " + tr["tool_count"]);
        var tools = chat["tools"]?.AsArray() ?? throw new Exception("sem tools");
        TestContext.Check(tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == "read_sensor"), "sensor achatado");
        TestContext.Check(tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == "publish_event"), "fila achatada");
    }
}
