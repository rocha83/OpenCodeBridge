// ===========================================================================
//  Provas A/B das flags do harness da bridge (bateria WAR).
//  Cada teste prova o valor da flag na marra: primeiro o cenario SEM o arg
//  (comportamento indesejado/falha), depois COM o arg (comportamento correto).
//  Upstream e' um stub HTTP deterministico (sem GPU, sem llama), entao roda em
//  segundos e pode entrar na bateria antes dos E2E caros.
// ===========================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

internal static class E2EFlagProofTests
{
    const string Key = "guest@123";
    const string Model = "Gemma4-E4B-Q4-build";

    // ---------------------------------------------------------------- stub llama
    // Responde /v1/chat/completions com JSON unico (a bridge pede stream=false ao
    // upstream). Guarda as requests vistas para os asserts de cauda/sistema.
    sealed class Stub : IDisposable
    {
        readonly HttpListener _l;
        public int Port { get; }
        int _calls;
        public int Calls => _calls;
        public ConcurrentQueue<JsonObject> Seen { get; } = new();
        public Func<int, string> Respond { get; set; } = _ => Completion("ok");

        public Stub(Func<int, string> respond)
        {
            Respond = respond;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _l = new HttpListener();
            _l.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _l.Start();
            _ = Task.Run(Loop);
        }

        async Task Loop()
        {
            while (_l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _l.GetContextAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        string body;
                        using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                            body = await r.ReadToEndAsync();
                        JsonObject? req = null;
                        try { req = JsonNode.Parse(body)?.AsObject(); } catch { }
                        int n = Interlocked.Increment(ref _calls);
                        if (req is not null) Seen.Enqueue(req);
                        string resp = Respond(n);
                        byte[] bytes = Encoding.UTF8.GetBytes(resp);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes);
                        ctx.Response.Close();
                    }
                    catch { try { ctx.Response.Abort(); } catch { } }
                });
            }
        }

        public string SystemText() => string.Join("\n", Seen.Select(r =>
            string.Join("\n", (r["messages"]?.AsArray() ?? new JsonArray())
                .Where(m => m?["role"]?.GetValue<string>() == "system")
                .Select(m => m?["content"]?.GetValue<string>() ?? ""))));

        public void Dispose()
        {
            try { _l.Stop(); _l.Close(); } catch { }
        }
    }

    static string Completion(string content, int prompt = 10, int completion = 5, string? reasoning = null)
    {
        var msg = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (reasoning is not null) msg["reasoning_content"] = reasoning;
        return new JsonObject
        {
            ["id"] = "stub",
            ["object"] = "chat.completion",
            ["model"] = "stub",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = msg, ["finish_reason"] = "stop" }),
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = prompt,
                ["completion_tokens"] = completion,
                ["total_tokens"] = prompt + completion,
            },
        }.ToJsonString();
    }

    // ------------------------------------------------------------------- bridge
    static string BridgeDll()
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            string p = Path.Combine(probe.FullName, "Rochas.OpenCodeBridge", "bin", "Release", "net10.0", "Rochas.OpenCodeBridge.dll");
            if (File.Exists(p)) return p;
            probe = probe.Parent;
        }
        throw new Exception("bridge dll nao encontrada: dotnet build -c Release Rochas.OpenCodeBridge.slnx");
    }

    sealed class Bridge : IDisposable
    {
        readonly Process _p;
        public int Port { get; }
        public string Log { get; }

        public Bridge(int stubPort, params string[] extra)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Log = Path.Combine(Path.GetTempPath(), "opencode", "flagproof", $"bridge-{Port}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(Log)!);
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(BridgeDll())!,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            foreach (string a in new[]
            {
                "exec", BridgeDll(),
                "--listen", "127.0.0.1", "--port", Port.ToString(),
                "--upstream", $"http://127.0.0.1:{stubPort}",
                "--model", Model, "--thinking", "events", "--temperature", "0.5",
                "--max-tokens", "2048", "--context-guard", "0", "--api-key", Key,
                "--passthrough-responses", "true", "--harness-tails", "0", "--legacy-harness", "0",
                "--anti-repeat", "0", "--anti-repeat-retry", "0", "--coerce-text-tools", "0",
                "--coerce-narrated", "0", "--tool-strategy", "0", "--anti-repeat-window", "512",
                "--tail-scope", "off", "--log", Log,
            }) psi.ArgumentList.Add(a);
            foreach (string a in extra) psi.ArgumentList.Add(a);
            _p = Process.Start(psi) ?? throw new Exception("nao subiu a bridge de prova");
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);
            for (int i = 0; i < 80; i++)
            {
                try
                {
                    using var r = http.GetAsync($"http://127.0.0.1:{Port}/api/status").GetAwaiter().GetResult();
                    if ((int)r.StatusCode == 200) return;
                }
                catch { }
                if (_p.HasExited) throw new Exception("bridge de prova morreu: " + Tail(Log));
                Thread.Sleep(250);
            }
            throw new Exception("timeout subindo a bridge de prova na porta " + Port);
        }

        public void Dispose()
        {
            try { if (!_p.HasExited) _p.Kill(true); } catch { }
            try { _p.WaitForExit(5000); } catch { }
        }
    }

    static string Tail(string path, int lines = 8)
    {
        try
        {
            string[] all = File.ReadAllLines(path);
            return string.Join("\n", all.TakeLast(lines));
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ requests
    static JsonObject ChatReq(string user = "teste", JsonArray? tools = null, string? model = null)
    {
        var req = new JsonObject
        {
            ["model"] = model ?? Model,
            ["stream"] = false,
            ["temperature"] = 0.1,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "Voce e um assistente." },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        if (tools is not null) req["tools"] = tools;
        return req;
    }

    static JsonArray ReadFileTool() => new(
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "read_file",
                ["description"] = "le um arquivo",
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
                    ["required"] = new JsonArray("path"),
                },
            },
        });

    static async Task<(int Status, JsonObject Body, string Raw)> Chat(int port, JsonObject req)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);
        var r = await http.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions",
            new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json"));
        string raw = await r.Content.ReadAsStringAsync();
        JsonObject body;
        try { body = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject(); }
        catch { body = new JsonObject(); }
        return ((int)r.StatusCode, body, raw);
    }

    static JsonObject ResponsesReq(string user = "teste", JsonArray? tools = null, string? model = null)
    {
        var req = new JsonObject
        {
            ["model"] = model ?? Model,
            ["stream"] = false,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = user })
            }),
        };
        if (tools is not null) req["tools"] = tools;
        return req;
    }

    static JsonArray ReadFileToolResponses() => new(
        new JsonObject
        {
            ["type"] = "function",
            ["name"] = "read_file",
            ["description"] = "le um arquivo",
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } },
                ["required"] = new JsonArray("path"),
            },
        });

    static async Task<(int Status, JsonObject Body, string Raw)> Responses(int port, JsonObject req)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Key);
        var r = await http.PostAsync($"http://127.0.0.1:{port}/v1/responses",
            new StringContent(req.ToJsonString(), Encoding.UTF8, "application/json"));
        string raw = await r.Content.ReadAsStringAsync();
        JsonObject body;
        try { body = JsonNode.Parse(raw)?.AsObject() ?? new JsonObject(); }
        catch { body = new JsonObject(); }
        return ((int)r.StatusCode, body, raw);
    }

    // =========================================================== --context-guard
    public static async Task ContextGuard()
    {
        using var stub = new Stub(_ => Completion("ok", prompt: 9000));
        await Task.Yield();

        using (var off = new Bridge(stub.Port, "--context-tokens", "4096"))
        {
            var (s, _, _) = await Chat(off.Port, ChatReq());
            TestContext.Check(s == 200, $"SEM --context-guard o excesso passa reto (status {s})");
        }

        using (var on = new Bridge(stub.Port, "--context-guard", "1", "--context-tokens", "4096", "--context-threshold", "0.9"))
        {
            var (s, b, raw) = await Chat(on.Port, ChatReq());
            TestContext.Check(s == 413, $"COM --context-guard 1 o excesso e barrado (status {s}): {raw[..Math.Min(160, raw.Length)]}");
            TestContext.Check((b["error"]?["code"]?.GetValue<string>() ?? "") == "context_compact_required",
                "413 traz code=context_compact_required");
            TestContext.Check(File.ReadAllText(on.Log).Contains("\"event\":\"context-guard\""),
                "evento context-guard registrado no log (sonda)");
        }
        await Task.CompletedTask;
    }

    // ========================================================== --anti-repeat-retry
    static string Repeated() => string.Join(" ", Enumerable.Repeat("alfa beta gama delta", 160));

    public static async Task AntiRepeatRetry()
    {
        using var stub = new Stub(n => Completion(n == 1 ? Repeated() : "OK-corrigido"));

        using (var off = new Bridge(stub.Port))
        {
            int before = stub.Calls;
            var (s, b, _) = await Chat(off.Port, ChatReq("repita"));
            string content = b["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            TestContext.Check(s == 200, $"SEM anti-repeat a resposta volta (status {s})");
            TestContext.Check(stub.Calls - before == 1, $"SEM flag: 1 chamada ao upstream (foram {stub.Calls - before})");
            TestContext.Check(content.Contains("alfa beta"), "SEM flag: o texto repetido vaza para o cliente");
        }

        using var stubOn = new Stub(n => Completion(n == 1 ? Repeated() : "OK-corrigido"));
        using (var on = new Bridge(stubOn.Port, "--anti-repeat", "1", "--anti-repeat-retry", "1", "--anti-repeat-abort", "0"))
        {
            int before = stubOn.Calls;
            var (s, b, raw) = await Chat(on.Port, ChatReq("repita"));
            string content = b["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            TestContext.Check(s == 200, $"COM anti-repeat a resposta volta (status {s}): {raw[..Math.Min(160, raw.Length)]}");
            TestContext.Check(stubOn.Calls - before >= 2, $"COM flag: retry bate no upstream de novo (foram {stubOn.Calls - before})");
            TestContext.Check(content.Contains("OK-corrigido"), "COM flag: vale a segunda geracao, nao a repetida");
            string log = File.ReadAllText(on.Log);
            TestContext.Check(log.Contains("\"event\":\"anti-repeat\""), "evento anti-repeat no log");
            TestContext.Check(log.Contains("\"event\":\"anti-repeat-retry\""), "evento anti-repeat-retry no log");
        }
    }

    // =========================================================== --coerce-narrated
    public static async Task CoerceNarrated()
    {
        const string narrated = "Vou ler o arquivo agora.\n[Assistant tool call]: read_file({\"path\":\"app/x.txt\"})";
        using var stub = new Stub(_ => Completion(narrated));

        using (var off = new Bridge(stub.Port))
        {
            var (s, b, _) = await Responses(off.Port, ResponsesReq("leia app/x.txt", ReadFileToolResponses(), "Gemma4-E4B-Q4"));
            int calls = (b["output"]?.AsArray() ?? new JsonArray())
                .Count(o => o?["type"]?.GetValue<string>() == "function_call");
            TestContext.Check(s == 200, $"SEM --coerce-narrated responde 200 (status {s})");
            TestContext.Check(calls == 0, $"SEM flag: a chamada narrada NAO vira tool_call (tool_calls={calls})");
        }

        using (var on = new Bridge(stub.Port, "--coerce-narrated", "1"))
        {
            var (s, b, raw) = await Responses(on.Port, ResponsesReq("leia app/x.txt", ReadFileToolResponses(), "Gemma4-E4B-Q4"));
            var arr = b["output"]?.AsArray() ?? new JsonArray();
            string name = arr.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "function_call")?["name"]?.GetValue<string>() ?? "";
            TestContext.Check(s == 200, $"COM --coerce-narrated responde 200 (status {s}): {raw[..Math.Min(160, raw.Length)]}");
            TestContext.Check(name == "read_file", $"COM flag: tool_call coerced read_file (veio '{name}')");
            TestContext.Check(File.ReadAllText(on.Log).Contains("\"event\":\"coerced\""), "evento coerced no log");
        }
    }

    // ================================================================ --tail-scope
    public static async Task TailScope()
    {
        using var stub = new Stub(_ => Completion("ok"));

        using (var off = new Bridge(stub.Port)) // base ja sobe com --tail-scope off
        {
            var (s, _, _) = await Chat(off.Port, ChatReq());
            string sys = stub.SystemText();
            TestContext.Check(s == 200, $"SEM cauda responde 200 (status {s})");
            TestContext.Check(!sys.Contains("MODO DE USO DAS TOOLS"), "SEM --tail-scope all o system prompt nao leva a cauda de tools");
        }

        using (var on = new Bridge(stub.Port, "--harness-tails", "1", "--tail-scope", "all"))
        {
            var (s, _, _) = await Chat(on.Port, ChatReq());
            string sys = stub.SystemText();
            TestContext.Check(s == 200, $"COM cauda responde 200 (status {s})");
            TestContext.Check(sys.Contains("MODO DE USO DAS TOOLS"), "COM --tail-scope all a cauda de tools chega ao upstream");
        }
    }
}
