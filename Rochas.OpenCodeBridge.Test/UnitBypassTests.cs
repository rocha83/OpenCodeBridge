using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Cenario complexo multi-turno nos dois canais: bypass (sanitize) e pipe antigo (translate).</summary>
internal static class UnitBypassTests
{
    static JsonObject ComplexInput() => new()
    {
        ["model"] = "openai/qwen3.5-9b",
        ["instructions"] = "Voce e um backend senior. Respostas curtas.",
        ["input"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Quanto e 20 + 22?" })
            },
            new JsonObject
            {
                ["type"] = "reasoning", ["id"] = "rs_hist1",
                ["summary"] = new JsonArray(),
                ["content"] = "pensei que era soma"
            },
            new JsonObject
            {
                ["type"] = "message", ["role"] = "assistant",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Vou somar." })
            },
            new JsonObject
            {
                ["type"] = "reasoning", ["id"] = "rs_hist2",
                ["summary"] = new JsonArray(),
                ["content"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = "resumo" })
            },
            new JsonObject
            {
                ["type"] = "function_call", ["id"] = "call_hist1", ["call_id"] = "call_hist1",
                ["name"] = "soma", ["arguments"] = "{\"a\":20,\"b\":22}"
            },
            new JsonObject
            {
                ["type"] = "function_call_output", ["call_id"] = "call_hist1", ["output"] = "42"
            },
            new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "E 1 + 1?" })
            }),
        ["tools"] = new JsonArray(
            TestContext.SomaToolResponses(),
            new JsonObject
            {
                ["type"] = "namespace", ["name"] = "git",
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "git_log", ["description"] = "Mostra log",
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                })
            })
    };

    static void CheckNoReasoning(JsonArray items, string where)
    {
        foreach (var it in items)
            TestContext.Check(!(it is JsonObject o && o["type"]?.GetValue<string>() == "reasoning"),
                $"sem reasoning em {where}");
    }

    public static async Task SanitizeDropsReasoning()
    {
        var res = await TestContext.PostObj(TestContext.Bridge + "/api/sanitize", ComplexInput());
        TestContext.Check(res["dropped_reasoning"]?.GetValue<int>() == 2, "dropped 2, veio: " + res["dropped_reasoning"]);
        var input = res["input"]?.AsArray() ?? throw new Exception("sem input");
        TestContext.Check(input.Count == 5, "7 itens - 2 reasoning = 5, veio: " + input.Count);
        CheckNoReasoning(input, "sanitize");
        TestContext.Check(input.Any(i => i?["type"]?.GetValue<string>() == "function_call"
            && i?["call_id"]?.GetValue<string>() == "call_hist1"), "function_call preservada");
        TestContext.Check(input.Any(i => i?["type"]?.GetValue<string>() == "function_call_output"), "output preservado");
    }

    public static async Task TranslateDropsReasoning()
    {
        var tr = await TestContext.PostObj(TestContext.Bridge + "/api/translate", ComplexInput());
        var chat = tr["chat_request"]?.AsObject() ?? throw new Exception("sem chat_request");
        var msgs = chat["messages"]?.AsArray() ?? throw new Exception("sem messages");
        foreach (var m in msgs)
        {
            string c = m?["content"]?.GetValue<string>() ?? "";
            TestContext.Check(!c.Contains("pensei que era soma") && !c.Contains("resumo"), "thinking fora das messages");
        }
        TestContext.Check(msgs.Any(m => m?["role"]?.GetValue<string>() == "tool"
            && m?["tool_call_id"]?.GetValue<string>() == "call_hist1"), "tool preserva call_id");
        var tools = chat["tools"]?.AsArray() ?? throw new Exception("sem tools");
        TestContext.Check(tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == "soma"), "soma convertida");
        TestContext.Check(tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == "git_log"), "namespace achatado");
    }
}
