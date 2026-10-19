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
    public static string? ApiKey;
    public static string? Jwt;
    // 20 min: os E2E enterprise (crud-*/garnet/worker) geram muito e, sob carga da suite
    // inteira, passavam de 600s (visto 16/10); isolados ficam em ~110-215s.
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    // Modelo servido pela bridge alvo (qwen3|coder): expectativas por perfil.
    public static string ServedModel = "";
    public static bool ServedIsCoder => ServedModel.Contains("coder", StringComparison.OrdinalIgnoreCase);
    // Role alvo da suite (default build): a suite chama o modelo com o sufixo
    // de role para exercitar o perfil real (temp/thinking) do Build.
    public static string Role = "build";
    // --model explicito vence; sem ele, aplica o sufixo de role ao servido.
    public static string Model = "";
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
