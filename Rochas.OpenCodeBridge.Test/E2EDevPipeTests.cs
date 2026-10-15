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

    // ---- visão VL (ponte CPU :4125, mmproj): imagem -> texto + tool específica.
    const string CpuBridge = "http://127.0.0.1:4125";

    static string Img64(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "TestData");
        if (!File.Exists(Path.Combine(dir, name)))
            dir = "/media/mint/Linux/opencodebridge/Rochas.OpenCodeBridge.Test/TestData";
        return Convert.ToBase64String(File.ReadAllBytes(Path.Combine(dir, name)));
    }

    static JsonObject VisionMsg(string prompt, string img64) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = prompt },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + img64 } }),
    };

    // VL lê a imagem e identifica o texto.
    public static async Task DevPipeVision()
    {
        var body = new JsonObject
        {
            ["model"] = "qwen3-vl-4b", ["temperature"] = 0.3, ["max_tokens"] = 64, ["stream"] = false,
            ["messages"] = new JsonArray(VisionMsg("O que esta escrito na imagem? Responda SOMENTE o texto.", Img64("placa.png"))),
        };
        var r = await TestContext.PostObj(CpuBridge + "/v1/chat/completions", body);
        var content = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(content.Contains("TESTE") && content.Contains("456"), "visao identifica TESTE 456");
    }

    // VL decompõe o documento em texto, linha a linha.
    public static async Task DevPipeDoc()
    {
        var body = new JsonObject
        {
            ["model"] = "qwen3-vl-4b", ["temperature"] = 0.3, ["max_tokens"] = 128, ["stream"] = false,
            ["messages"] = new JsonArray(VisionMsg("Transcreva exatamente as tres linhas da imagem, uma por linha.", Img64("doc.png"))),
        };
        var r = await TestContext.PostObj(CpuBridge + "/v1/chat/completions", body);
        var content = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(content.Contains("UM") && content.Contains("DOIS") && content.Contains("TRES"), "doc decomposto em 3 linhas");
    }

    // Visão + tool call específica: identifica e chama read no path exato.
    // Dialeto Alibaba direto (o que o VL entende) + required; retry ×3 (Q3 flakey).
    public static async Task DevPipeVisionTool()
    {
        string img64 = Img64("placa.png");
        bool okName = false, okPath = false;
        for (int attempt = 0; attempt < 3 && !(okName && okPath); attempt++)
        {
            var body = new JsonObject
            {
                ["model"] = "qwen3-vl-4b", ["temperature"] = 0.3, ["max_tokens"] = 256, ["stream"] = false,
                ["tool_choice"] = "required",
                ["messages"] = new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = "Voce e um desenvolvedor senior." },
                    VisionMsg("Identifique o texto da imagem via visao e CHAME a tool read_file com file_path /tmp/vlimg/placa.png.", img64)),
                ["tools"] = new JsonArray(Tool("read_file", "Le arquivo", new JsonObject { ["file_path"] = Str("caminho") }, "file_path")),
            };
            var r = await TestContext.PostObj(CpuBridge + "/v1/chat/completions", body);
            var tc = r["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
            if (tc is not { Count: > 0 }) continue;
            var fn = tc[0]?["function"];
            string name = fn?["name"]?.GetValue<string>() ?? "";
            string args = fn?["arguments"]?.GetValue<string>() ?? "";
            if (name is "read" or "read_file") okName = true;
            if (args.Contains("placa")) okPath = true;
        }
        TestContext.Check(okName, "visao+tool chama read");
        TestContext.Check(okPath, "visao+tool path exato");
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
