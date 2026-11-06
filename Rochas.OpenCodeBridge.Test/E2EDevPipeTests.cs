using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// DevPipe E2E (cenário dev completo): leitura, escrita, navegação em pastas,
// repositório git, pacotes/versões e internet — modelo vivo via bridge.
internal static class E2EDevPipeTests
{
    static string Model() => TestContext.SuiteModel;

    static JsonArray DevTools() => TestContext.Catalog("glob", "read", "shell");

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
            ["model"] = Model(), ["temperature"] = 0.1, ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = $"Liste os .md em {dir} com o glob e leia o primeiro. Responda SOMENTE com a primeira linha dele."
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
            ["model"] = Model(), ["temperature"] = 0.1, ["stream"] = false,
            ["messages"] = new JsonArray(
                t1["messages"]!.AsArray()[0]!.DeepClone(),
                new JsonObject
                {
                    ["role"] = "assistant", ["content"] = null,
                    ["tool_calls"] = JsonNode.Parse(tc1!.ToJsonString())!.AsArray(),
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
            ["model"] = Model(), ["temperature"] = 0.1, ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user", ["content"] = "O conteúdo lido foi 'DEVPIPE-OK'. Responda SOMENTE: DEVPIPE-FIM"
            }),
        };
        var r3 = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions", t3);
        var content3 = r3["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(content3.Contains("DEVPIPE-FIM"), "devpipe final DEVPIPE-FIM");
    }

    // ---- visão VL: GPU1 (Q4, llama :4100 com --mmproj). GPU2 nao tem vision.
    static string VisionBridge => Environment.GetEnvironmentVariable("VISION_BRIDGE") ?? TestContext.Bridge;

    static string Img64(string name)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "TestData");
        if (!File.Exists(Path.Combine(dir, name)))
        {
            var probe = new DirectoryInfo(AppContext.BaseDirectory);
            while (probe is not null && !File.Exists(Path.Combine(probe.FullName, "TestData", name))) probe = probe.Parent;
            if (probe is not null) dir = Path.Combine(probe.FullName, "TestData");
        }
        return Convert.ToBase64String(File.ReadAllBytes(Path.Combine(dir, name)));
    }

    // Gate de visao: le /props do upstream (o que a bridge status informa) e exige
    // modalities.vision=true. Sem mmproj, SKIP com motivo — nao FAIL.
    static async Task<(string Bridge, string Model)> VisionReady()
    {
        var st = await TestContext.GetObj(VisionBridge + "/api/status");
        string model = st["model"]?.GetValue<string>() ?? "Gemma4-E4B-Q4";
        string upstream = st["upstream"]?.GetValue<string>() ?? "";
        if (upstream.Length > 0)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                http.DefaultRequestHeaders.Authorization = TestContext.Http.DefaultRequestHeaders.Authorization;
                var props = JsonNode.Parse(await http.GetStringAsync(upstream.TrimEnd('/') + "/props"));
                if (props?["modalities"]?["vision"]?.GetValue<bool>() != true)
                    TestContext.Skip($"{model}: upstream sem modalities.vision (mmproj desligado) em {VisionBridge}");
            }
            catch { /* upstream sem /props: segue para o teste real */ }
        }
        return (VisionBridge, model);
    }

    static JsonObject VisionMsg(string prompt, string img64) => new()
    {
        ["role"] = "user",
        ["content"] = new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = prompt },
            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64," + img64 } }),
    };

    // O VL pode transcrever "TRÊS"/"tres"/"Tres": compara sem acento e sem caixa
    // para nao transformar transcricao correta em FAIL.
    // InvariantGlobalization=true deixa string.Normalize sem efeito; o mapa
    // explicito cobre os acentos que o VL pode emitir na transcricao.
    static string NormText(string s)
    {
        var b = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            char f = c switch
            {
                'á' or 'à' or 'â' or 'ã' or 'ä' or 'Á' or 'À' or 'Â' or 'Ã' or 'Ä' => 'a',
                'é' or 'è' or 'ê' or 'ë' or 'É' or 'È' or 'Ê' or 'Ë' => 'e',
                'í' or 'ì' or 'î' or 'ï' or 'Í' or 'Ì' or 'Î' or 'Ï' => 'i',
                'ó' or 'ò' or 'ô' or 'õ' or 'ö' or 'Ó' or 'Ò' or 'Ô' or 'Õ' or 'Ö' => 'o',
                'ú' or 'ù' or 'û' or 'ü' or 'Ú' or 'Ù' or 'Û' or 'Ü' => 'u',
                'ç' or 'Ç' => 'c',
                'ñ' or 'Ñ' => 'n',
                _ => c
            };
            b.Append(char.ToLowerInvariant(f));
        }
        return b.ToString();
    }

    // VL lê a imagem e identifica o texto.
    public static async Task DevPipeVision()
    {
        var (bridge, model) = await VisionReady();
        var body = new JsonObject
        {
            ["model"] = model, ["temperature"] = 0.1, ["stream"] = false,
            ["messages"] = new JsonArray(VisionMsg("O que esta escrito na imagem? Responda SOMENTE o texto.", Img64("placa.png"))),
        };
        var r = await TestContext.PostObj(bridge + "/v1/chat/completions", body);
        var content = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var norm = NormText(content);
        TestContext.Check(norm.Contains("teste") && norm.Contains("456"), "visao identifica TESTE 456");
    }

    // VL decompõe o documento em texto, linha a linha.
    public static async Task DevPipeDoc()
    {
        var (bridge, model) = await VisionReady();
        var body = new JsonObject
        {
            ["model"] = model, ["temperature"] = 0.1, ["stream"] = false,
            ["messages"] = new JsonArray(VisionMsg("Transcreva exatamente as tres linhas da imagem, uma por linha.", Img64("doc.png"))),
        };
        var r = await TestContext.PostObj(bridge + "/v1/chat/completions", body);
        var content = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var norm = NormText(content);
        Console.WriteLine($"[devpipe] doc: {content.Replace("\r", " ").Replace("\n", " | ")}");
        TestContext.Check(norm.Contains("um") && norm.Contains("dois") && norm.Contains("tres"), "doc decomposto em 3 linhas");
    }

    // Visão + tool call específica: identifica e chama read no path exato.
    // Dialeto Alibaba direto (o que o VL entende) + required; retry ×3 (Q3 flakey).
    public static async Task DevPipeVisionTool()
    {
        var (bridge, model) = await VisionReady();
        string img64 = Img64("placa.png");
        // Orcamento e politica da bridge/perfil (16k no chat real), nao do teste:
        // 512/1024 foram residuo do diagnostico de truncamento e nao representam
        // o cliente da Web UI, que nunca ajusta max_tokens por enunciado.
        string toolModel = model.EndsWith("-build", StringComparison.OrdinalIgnoreCase) ? model : model + "-build";
        var body = new JsonObject
        {
            ["model"] = toolModel, ["temperature"] = 0.1, ["stream"] = false,
            ["tool_choice"] = "required",
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "Voce e um desenvolvedor senior." },
                VisionMsg("Identifique o texto da imagem via visao e CHAME a tool read com path /tmp/vlimg/placa.png.", img64)),
            ["tools"] = TestContext.Catalog("read"),
        };
        var r = await TestContext.PostObj(bridge + "/v1/chat/completions", body);
        string finish = r["choices"]?[0]?["finish_reason"]?.GetValue<string>() ?? "";
        string content = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        var tc = r["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
        var fn = tc is { Count: > 0 } ? tc[0]?["function"] : null;
        string name = fn?["name"]?.GetValue<string>() ?? "";
        string args = fn?["arguments"]?.GetValue<string>() ?? "";
        Console.WriteLine($"[devpipe] vision-tool: model={toolModel} finish={finish} content={content.Length} name={name} args={args}");

        // Regressao do DRY: com --dry-multiplier > 0 o modelo mutava o
        // identificador copiado do prompt (placa.png -> placa.ng/_ng/.PNG/
        // .jpg/.txt, deterministico). A infra roda DRY 0.0 (LAB-SETUP §10) e o
        // path exato e o aceite; o retry x3 absorve variacao residual do VL.
        bool nameOk = name == "read";
        bool exact = ArgsMatchPath(args, "/tmp/vlimg/placa.png");
        for (int attempt = 1; attempt <= 3 && !(nameOk && exact); attempt++)
        {
            var retry = await TestContext.PostObj(bridge + "/v1/chat/completions", body);
            var retryTc = retry["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
            var retryFn = retryTc is { Count: > 0 } ? retryTc[0]?["function"] : null;
            name = retryFn?["name"]?.GetValue<string>() ?? "";
            args = retryFn?["arguments"]?.GetValue<string>() ?? "";
            Console.WriteLine($"[devpipe] vision-tool retry {attempt}: name={name} args={args}");
            nameOk = name == "read";
            exact = ArgsMatchPath(args, "/tmp/vlimg/placa.png");
        }

        TestContext.Check(nameOk, "visao+tool chama read");
        TestContext.Check(exact, "visao+tool path exato");
    }

    // Argumentos podem vir como JSON valido ou prosa com o path; compara o
    // valor exato (nao prefixo) para nao aceitar variacao de extensao.
    private static bool ArgsMatchPath(string args, string path)
    {
        try
        {
            var node = JsonNode.Parse(args);
            string? value = node?["file_path"]?.GetValue<string>()
                            ?? node?["path"]?.GetValue<string>();
            return string.Equals(value, path, StringComparison.Ordinal);
        }
        catch
        {
            return args.Contains(path, StringComparison.Ordinal);
        }
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
        // gh e opcional: ausente no host/allowlist nao invalida a infra (git+fetch).
        var gh = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "shell", ["arguments"] = new JsonObject { ["command"] = "gh --version" } });
        if (gh["ok"]?.GetValue<bool>() == true)
            TestContext.Check((gh["output"]?.GetValue<string>() ?? "").Contains("gh version"), "infra gh version");
        else
            Console.WriteLine("  [infra] gh indisponivel/negado - assert gh pulado");

        var fetch = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "fetch", ["arguments"] = new JsonObject { ["url"] = "https://example.com" } });
        TestContext.Check(fetch["ok"]?.GetValue<bool>() == true
            && (fetch["output"]?.GetValue<string>() ?? "").Contains("Example Domain"), "infra fetch example.com");
    }
}
