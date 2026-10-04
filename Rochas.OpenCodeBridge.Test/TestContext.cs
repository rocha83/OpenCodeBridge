// Contexto compartilhado da suite (estado + runner + helpers HTTP + fixture soma).

using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Estado global, runner e helpers HTTP da suite (BCL only).</summary>
internal static class TestContext
{
    public static string Bridge = "http://127.0.0.1:4124";
    public static string Vllm = "http://127.0.0.1:4100";
    public static bool SkipE2E;
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // Modelo servido pela bridge alvo (qwen3|coder): expectativas por perfil.
    public static string ServedModel = "";
    public static bool ServedIsCoder => ServedModel.Contains("coder", StringComparison.OrdinalIgnoreCase);
    static int Passed;
    static int Failed;

    public static void Run(string name, Func<Task> fn)
    {
        var t0 = DateTime.UtcNow;
        try
        {
            fn().GetAwaiter().GetResult();
            Passed++;
            Console.WriteLine($"PASS {name} ({(DateTime.UtcNow - t0).TotalSeconds:F1}s)");
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
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
    }

    public static JsonObject SomaToolResponses() => new()
    {
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
        Console.WriteLine($"[test] PASS={Passed} FAIL={Failed}");
        return Failed == 0 ? 0 : 1;
    }
}
