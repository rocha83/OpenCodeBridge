using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// DevPipe E2E (cenário dev completo): leitura, escrita, navegação em pastas,
// repositório git, pacotes/versões e internet — modelo vivo via bridge.
internal static class E2EDevPipeTests
{
    static string Model() => TestContext.ServedModel.Length > 0 ? TestContext.ServedModel : "qwen3.5-9b";

    static JsonObject Tool(string name, string desc, JsonObject props, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name, ["description"] = desc,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req }
            }
        };
    }

    static JsonObject Str(string desc = "") => new() { ["type"] = "string", ["description"] = desc };

    static JsonArray DevTools() => new()
    {
        Tool("glob", "Lista arquivos por padrão", new JsonObject { ["pattern"] = Str("padrão glob") }, "pattern"),
        Tool("read", "Lê arquivo", new JsonObject { ["path"] = Str("caminho") }, "path"),
        Tool("shell", "Executa comando único", new JsonObject { ["command"] = Str("comando") }, "command"),
    };

    // Turno 1: modelo deve chamar glob no fixture; turno 2: alimenta glob e
    // exige read; turno 3: alimenta conteúdo e exige o marcador no final.
    public static async Task DevPipeTools()
    {
        string dir = Path.Combine(Path.GetTempPath(), "devpipe");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.md"), "DEVPIPE-OK\nlinha dois\n");
        File.WriteAllText(Path.Combine(dir, "b.md"), "outro\n");

        var t1 = new JsonObject
        {
            ["model"] = Model(), ["temperature"] = 0.3, ["max_tokens"] = 512, ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"Liste via tool glob os .md em {dir} e depois leia via tool read o primeiro. Responda SOMENTE com a primeira linha dele."
            }),
            ["tools"] = DevTools(),
        };
        var r1 = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions", t1);
        var tc1 = r1["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
        TestContext.Check(tc1 is { Count: > 0 }, "devpipe turno1 tem tool_calls");
        var names1 = tc1!.Select(t => t?["function"]?["name"]?.GetValue<string>() ?? "").ToList();
        TestContext.Check(names1.Contains("glob"), "devpipe turno1 chama glob");

        var t2 = new JsonObject
        {
            ["model"] = Model(), ["temperature"] = 0.3, ["max_tokens"] = 512, ["stream"] = false,
            ["messages"] = new JsonArray(
                t1["messages"]!.AsArray()[0]!.DeepClone(),
                new JsonObject
                {
                    ["role"] = "assistant", ["content"] = null,
                    ["tool_calls"] = JsonNode.Parse(tc1.ToJsonString())!.AsArray(),
                },
                new JsonObject { ["role"] = "tool", ["tool_call_id"] = tc1[0]?["id"]?.GetValue<string>() ?? "c1", ["content"] = "[\"a.md\", \"b.md\"]" }),
            ["tools"] = DevTools(),
        };
        var r2 = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions", t2);
        var tc2 = r2["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
        var names2 = (tc2 ?? new JsonArray()).Select(t => t?["function"]?["name"]?.GetValue<string>() ?? "").ToList();
        var content2 = r2["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(names2.Contains("read") || content2.Contains("DEVPIPE-OK"), "devpipe turno2 le ou finaliza");

        var t3 = new JsonObject
        {
            ["model"] = Model(), ["temperature"] = 0.3, ["max_tokens"] = 256, ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user", ["content"] = "O conteúdo lido foi 'DEVPIPE-OK'. Responda SOMENTE: DEVPIPE-FIM"
            }),
        };
        var r3 = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions", t3);
        var content3 = r3["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(content3.Contains("DEVPIPE-FIM"), "devpipe final DEVPIPE-FIM");
    }

    // Infra dev direto no executor da bridge: git, gh, fetch internet.
    public static async Task DevPipeInfra()
    {
        async Task<string> Exec(string name, JsonObject args)
        {
            var r = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
                new JsonObject { ["name"] = name, ["arguments"] = args });
            TestContext.Check(r["ok"]?.GetValue<bool>() == true, $"infra {name} ok");
            return r["output"]?.GetValue<string>() ?? "";
        }

        string git = await Exec("shell", new JsonObject { ["command"] = "git --version" });
        TestContext.Check(git.Contains("git version"), "infra git version");
        string gh = await Exec("shell", new JsonObject { ["command"] = "gh --version" });
        TestContext.Check(gh.Contains("gh version"), "infra gh version");

        var fetch = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "fetch", ["arguments"] = new JsonObject { ["url"] = "https://example.com" } });
        TestContext.Check(fetch["ok"]?.GetValue<bool>() == true
            && (fetch["output"]?.GetValue<string>() ?? "").Contains("Example Domain"), "infra fetch example.com");
    }
}
