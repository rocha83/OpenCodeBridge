// Contexto compartilhado da suite (estado + runner + helpers HTTP + fixture soma).

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Estado global, runner e helpers HTTP da suite (BCL only).</summary>
internal static class TestContext
{
    public static string Bridge = "http://127.0.0.1:4124";
    public static string Vllm = "http://127.0.0.1:4100";
    public static bool SkipE2E;
    public static string Only = "";
    public static string BeltsFilter = "";

    public static string? ApiKey;

    public static string? Jwt;
    // 20 min: os E2E enterprise (crud-*/garnet/worker) geram muito e, sob carga da suite
    // inteira, passavam de 600s (visto 16/10); isolados ficam em ~110-215s.
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(120) };

    // Modelo servido pela bridge alvo (qwen3|coder): expectativas por perfil.
    public static string ServedModel = "";
    public static bool ServedIsCoder => ServedModel.Contains("coder", StringComparison.OrdinalIgnoreCase);
    // Role alvo da suite (default build): a suite chama o modelo com o sufixo
    // de role para exercitar o perfil real (temp/thinking) do Build.
    public static string Role = "build";
    // --model explicito vence; sem ele, aplica o sufixo de role ao servido.
    public static string Model = "";
    // --standalone: desvia os E2E para o opencode real (mesmo enunciado),
    // em vez de HTTP direto na bridge. --standalone-agent escolhe o agent.
    public static bool Standalone;
    public static string StandaloneAgent = "build";
    public static int StandaloneTimeoutSec = 300;
    // --standalone-model explicito (provider/model); senao deriva do agent:
    // exec -> llama/ (CPU E2B), demais -> openai/ (GPU 12B).
    public static string StandaloneModel = "";
    // --standalone-provider: forca o provider do opencode (openai=Q4 :4124,
    // llama=Q3 :4125, cpu=:4126). Vazio = heuristica antiga (exec -> llama).
    public static string StandaloneProvider = "";
    public static string SuiteModel
    {
        get
        {
            if (Model.Length > 0) return Model;
            string m = ServedModel.Length > 0 ? ServedModel : "gemma-4-12b";
            bool hasRole = m.EndsWith("-plan") || m.EndsWith("-build") || m.EndsWith("-orch")
                || m.EndsWith("-moe") || m.EndsWith("-scripter") || m.EndsWith("-exec");
            return !hasRole && Role.Length > 0 ? m + "-" + Role : m;
        }
    }
    // Modo thinking da bridge (events|off): asserts de reasoning só em events.
    public static string ServedThinking = "events";
    /// <summary>Bridge com --legacy-harness 1: unico modo em que ha coercao de tools.</summary>
    public static bool ServedLegacy;

    /// <summary>Coercao de tool call so existe no modo legacy; fora dele e SKIP.</summary>
    public static void RequireCoercion()
    {
        if (!ServedLegacy) Skip("bridge sem --legacy-harness 1 (coercao desligada por design)");
    }
    public static bool WantsReasoning => ServedThinking == "events" && !ServedIsCoder;
    static int Passed;
    static int Failed;
    static int Skipped;

    /// <summary>Marca o teste como pulado (ex.: cobre caminho legado desligado).</summary>
    public sealed class SkipException : Exception
    {
        public SkipException(string reason) : base(reason) { }
    }

    public static void Skip(string reason) => throw new SkipException(reason);

    public static void Run(string name, Func<Task> fn)
    {
        if (Only.Length > 0 && !name.Contains(Only, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"SKIP {name} (filtro --only)");
            return;
        }
        var t0 = DateTime.UtcNow;
        try
        {
            fn().GetAwaiter().GetResult();
            Passed++;
            Console.WriteLine($"PASS {name} ({(DateTime.UtcNow - t0).TotalSeconds:F1}s)");
        }
        catch (SkipException ex)
        {
            Skipped++;
            Console.WriteLine($"SKIP {name} ({ex.Message})");
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
            if (Environment.GetEnvironmentVariable("E2E_STACK") == "1") Console.WriteLine(ex.ToString());
        }
    }

    public static void Check(bool cond, string msg)
    {
        if (!cond) throw new Exception("assert: " + msg);
    }

    /// <summary>Base do modelo servido, sem sufixo de role (ex.: gemma-4-12b).</summary>
    static string BaseModel
    {
        get
        {
            string m = ServedModel.Length > 0 ? ServedModel : "gemma-4-12b";
            foreach (var r in new[] { "-plan", "-build", "-orch", "-moe", "-scripter", "-exec" })
                if (m.EndsWith(r)) return m[..^r.Length];
            return m;
        }
    }

    /// <summary>provider/model derivado do agent standalone (exec -> llama/E2B; demais -> openai/GPU).</summary>
    static string AutoStandaloneModel()
    {
        string role = StandaloneAgent is "plan" or "build" or "orch" or "moe" or "scripter" or "exec"
            ? StandaloneAgent : "build";
        string provider = StandaloneProvider.Length > 0
            ? StandaloneProvider + "/"
            : (StandaloneAgent == "exec" ? "llama/" : "openai/");
        return provider + BaseModel + "-" + role;
    }

    /// <summary>Roda o mesmo enunciado pelo opencode real (standalone) e devolve stdout+stderr.</summary>
    public static string OpencodeRun(string prompt)
    {
        var psi = new ProcessStartInfo("opencode")
        {
            WorkingDirectory = Path.Combine(Path.GetTempPath(), "opencode", "standalone"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string model = StandaloneModel.Length > 0 ? StandaloneModel : AutoStandaloneModel();
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--standalone");   // servidor privado: nao depende do background service (evita "Timed out waiting for the background service to start")
        psi.ArgumentList.Add("--agent"); psi.ArgumentList.Add(StandaloneAgent);
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model);
        psi.ArgumentList.Add(prompt);
        Directory.CreateDirectory(psi.WorkingDirectory);
        using var p = Process.Start(psi) ?? throw new Exception("sem processo: opencode");
        string outp = p.StandardOutput.ReadToEnd();
        string errp = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(StandaloneTimeoutSec * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout opencode standalone"); }
        return outp + errp;
    }

    // --dual: roda cada teste de tools duas vezes, no modo "pure" (HTTP direto na
    // bridge, com a bridge executando as tools) e no modo "opencode" (opencode CLI
    // executando as tools). Sem --dual roda so "pure", como antes.
    public static bool Dual;
    public static string Mode = "pure";
    public static int ToolAgentSteps = 8;

    /// <summary>
    /// Roda o mesmo teste nos dois modos quando --dual esta ligado
    /// (sufixo -pure / -opencode no nome do caso).
    /// </summary>
    /// <param name="name">Nome do caso.</param>
    /// <param name="fn">Corpo do teste (le TestContext.Mode).</param>
    public static void RunDual(string name, Func<Task> fn)
    {
        if (!Dual) { Run(name, fn); return; }
        string prev = Mode;
        try
        {
            foreach (string mode in new[] { "pure", "opencode" })
            {
                Mode = mode;
                Run(name + "-" + mode, fn);
            }
        }
        finally { Mode = prev; }
    }

    /// <summary>Catálogo completo de tools de sistema (formato Responses).</summary>
    /// <returns>Array de tools.</returns>
    public static JsonArray ToolCatalog() => new()
    {
        Fn("shell", "Executa um comando de console. Um comando por chamada; sem heredoc.", new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string" }
        }, "command"),
        Fn("write", "Cria/sobrescreve um arquivo de texto.", new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["content"] = new JsonObject { ["type"] = "string" }
        }, "path", "content"),
        Fn("read", "Le um arquivo de texto.", new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" }
        }, "path"),
        Fn("edit", "Substitui um trecho exato de um arquivo.", new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["oldString"] = new JsonObject { ["type"] = "string" },
            ["newString"] = new JsonObject { ["type"] = "string" }
        }, "path", "oldString", "newString"),
        Fn("glob", "Lista arquivos por padrao. Nao aceita alternancia com |.", new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string" }
        }, "pattern"),
        Fn("grep", "Busca expressao regular no conteudo dos arquivos.", new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string" }
        }, "pattern")
    };

    static JsonObject Fn(string name, string description, JsonObject props, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = req
                }
            }
        };
    }

    /// <summary>
    /// Executa um enunciado agentico no modo corrente e devolve o texto final +
    /// histórico das tools executadas.
    /// pure     = loop HTTP na bridge (/v1/responses + /api/tools/execute);
    /// opencode = opencode run --standalone (o cliente executa as tools).
    /// </summary>
    /// <param name="prompt">Enunciado.</param>
    /// <param name="steps">Máximo de rodadas de tool no modo puro.</param>
    /// <returns>Texto final do assistente e as tools executadas.</returns>
    public static async Task<(string Text, List<string> Calls)> ToolAgent(string prompt, int steps = 0)
    {
        if (Mode == "opencode") return (OpencodeRun(prompt), new List<string>());
        int max = steps > 0 ? steps : ToolAgentSteps;
        var input = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt });
        var calls = new List<string>();
        string text = "";
        for (int step = 0; step < max; step++)
        {
            var res = await PostObj(Bridge + "/v1/responses", new JsonObject
            {
                ["model"] = SuiteModel,
                ["input"] = input.DeepClone(),
                ["tools"] = ToolCatalog(),
                ["stream"] = false
            });
            text = "";
            if (res["output"] is JsonArray output)
            {
                foreach (var item in output)
                {
                    string type = item?["type"]?.GetValue<string>() ?? "";
                    if (type == "message")
                        foreach (var c in item?["content"]?.AsArray() ?? new JsonArray())
                            if (c?["type"]?.GetValue<string>() == "output_text") text += c?["text"]?.GetValue<string>() ?? "";
                    else if (type == "function_call")
                    {
                        string callId = item?["call_id"]?.GetValue<string>() ?? item?["id"]?.GetValue<string>() ?? "";
                        string name = item?["name"]?.GetValue<string>() ?? "";
                        string args = item?["arguments"]?.GetValue<string>() ?? "{}";
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call", ["id"] = callId, ["call_id"] = callId,
                            ["name"] = name, ["arguments"] = args
                        });
                        JsonObject exec;
                        try
                        {
                            exec = await PostObj(Bridge + "/api/tools/execute",
                                new JsonObject { ["name"] = name, ["arguments"] = JsonNode.Parse(args) ?? new JsonObject() });
                        }
                        catch (Exception ex) { exec = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                        string outText = exec["ok"]?.GetValue<bool>() == true
                            ? (exec["output"]?.GetValue<string>() ?? "")
                            : "ERRO: " + (exec["error"]?.GetValue<string>() ?? "falha");
                        calls.Add($"{name}({args}) -> {(exec["ok"]?.GetValue<bool>() == true ? "ok" : "fail")}");
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = outText
                        });
                    }
                }
            }
            bool hasCall = input.Count > 0 && input[input.Count - 1]!["type"]?.GetValue<string>() == "function_call_output";
            if (!hasCall) break;
        }
        return (text, calls);
    }

    public static async Task<JsonObject> GetObj(string url)
    {
        using var res = await Http.GetAsync(url);
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"GET {url} -> {(int)res.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        return JsonNode.Parse(body)?.AsObject() ?? throw new Exception("GET resposta nao-JSON");
    }

    public static async Task<JsonObject> PostObj(string url, JsonNode body)
    {
        var t0 = DateTime.UtcNow;
        for (int attempt = 0; ; attempt++)
        {
            using var res = await Http.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            string text = await res.Content.ReadAsStringAsync();
            // Corpo vazio com 200 sob rajada (keep-alive): 1 retry após 2s.
            if (res.IsSuccessStatusCode && string.IsNullOrWhiteSpace(text) && attempt == 0)
            {
                Console.WriteLine($"  [retry] corpo vazio em {url}, tentando de novo...");
                await Task.Delay(2000);
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new Exception($"POST {url} -> {(int)res.StatusCode}: {text[..Math.Min(300, text.Length)]}");
            if (string.IsNullOrWhiteSpace(text)) throw new Exception($"POST {url} -> corpo vazio apos retry");
            var obj = JsonNode.Parse(text)?.AsObject() ?? throw new Exception("POST resposta nao-JSON");
        int outTok = obj["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        if (outTok > 0)
        {
            double secs = (DateTime.UtcNow - t0).TotalSeconds;
            Console.WriteLine($"  [tps] {outTok} tok / {secs:F1}s = {(secs > 0 ? outTok / secs : 0):F1} tok/s <- {url}");
        }
        return obj;
        } // for: retry unico em corpo vazio
    }

    /// <summary>
    /// Gera o scaffold do projeto pelo template `dotnet new` (mesmo conceito dos project
    /// templates do Visual Studio): cria o .csproj/.fsproj + o fonte template na pasta alvo.
    /// O harness faz isso (nao o modelo) para os testes nao dependerem de o modelo acertar o
    /// `dotnet new`; o valor do teste fica em escrever o fonte e iterar ate rodar.
    /// </summary>
    public static void ScaffoldProject(string name, string dir, string lang = "C#", string kind = "console", int secs = 180)
    {
        Directory.CreateDirectory(dir);
        string args = $"new {kind} -n {name} -o {dir}" + (lang.Equals("C#", StringComparison.OrdinalIgnoreCase) ? "" : $" -lang {lang}");
        var psi = new ProcessStartInfo("dotnet", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi) ?? throw new Exception("sem processo: dotnet new");
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout dotnet new " + name); }
        string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (p.ExitCode != 0) throw new Exception($"dotnet new {name} falhou ({p.ExitCode}): {outp[..Math.Min(300, outp.Length)]}");
    }

    public static JsonObject SomaToolResponses() => new()    {
        ["type"] = "function",
        ["name"] = "soma",
        ["description"] = "Soma dois inteiros",
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
    };

    public static int Summary()
    {
        Console.WriteLine($"[test] PASS={Passed} FAIL={Failed} SKIP={Skipped}");
        return Failed == 0 ? 0 : 1;
    }
}
