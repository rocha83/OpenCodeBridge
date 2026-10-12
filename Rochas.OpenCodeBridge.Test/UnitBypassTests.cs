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
TestContext.Check(input.Count == 6, "7 - 2 reasoning + 1 system(instructions) = 6, veio: " + input.Count);
        var first = input[0]?.AsObject() ?? throw new Exception("sem primeiro");
        TestContext.Check(first["role"]?.GetValue<string>() == "system", "instructions vira system inicial");
        CheckNoReasoning(input, "sanitize");
        TestContext.Check(input.Any(i => i?["type"]?.GetValue<string>() == "function_call"
            && i?["call_id"]?.GetValue<string>() == "call_hist1"), "function_call preservada");
        TestContext.Check(input.Any(i => i?["type"]?.GetValue<string>() == "function_call_output"), "output preservado");
    }

public static async Task SanitizeSystemFirst()
    {
        var body = ComplexInput();
        var input = body["input"]!.AsArray();
        input.Insert(2, new JsonObject
        {
            ["type"] = "message", ["role"] = "system",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "resumo do meio" })
        });
        var res = await TestContext.PostObj(TestContext.Bridge + "/api/sanitize", body);
        var clean = res["input"]?.AsArray() ?? throw new Exception("sem input");
        var first = clean[0]?.AsObject() ?? throw new Exception("sem primeiro");
        TestContext.Check(first["role"]?.GetValue<string>() == "system", "primeiro e system");
        string t = first["content"]?.AsArray().FirstOrDefault()?["text"]?.GetValue<string>() ?? "";
        TestContext.Check(t.Contains("resumo do meio"), "system do meio fundido no inicial");
        TestContext.Check(t.Contains("Voce e um backend senior"), "instructions fundida junto");
        var res2 = await TestContext.PostObj(TestContext.Bridge + "/api/sanitize", ComplexInput());
        TestContext.Check(res2["has_instructions"]?.GetValue<bool>() == false, "campo instructions removido");
        TestContext.Check(clean.Count(i => i?["type"]?.GetValue<string>() == "message"
            && (i?["role"]?.GetValue<string>() == "system" || i?["role"]?.GetValue<string>() == "developer")) == 1,
            "system unico");
    }

    public static async Task TranslateSystemFirst()
    {
        var body = ComplexInput();
        body["instructions"] = "instrucao base";
        var input = body["input"]!.AsArray();
        input.Add(new JsonObject
        {
            ["type"] = "message", ["role"] = "developer",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "regra tardia" })
        });
        var tr = await TestContext.PostObj(TestContext.Bridge + "/api/translate", body);
        var msgs = tr["chat_request"]?["messages"]?.AsArray() ?? throw new Exception("sem messages");
        TestContext.Check(msgs[0]?["role"]?.GetValue<string>() == "system", "chat abre com system");
        string sys = msgs[0]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(sys.Contains("instrucao base") && sys.Contains("regra tardia"), "system fundido: " + sys[..Math.Min(80, sys.Length)]);
        TestContext.Check(msgs.Count(m => m?["role"]?.GetValue<string>() == "system") == 1, "system unico no chat");
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
