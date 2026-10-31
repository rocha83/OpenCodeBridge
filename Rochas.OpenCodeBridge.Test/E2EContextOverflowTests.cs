using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Smoke que FORÇA o estouro de contexto no upstream (400 <c>exceed_context_size</c>)
/// e prova a escalada da bridge: 1) nudge, 2) compactação, 3) 413 quando nem ela
/// cabe. Sem este smoke o mecanismo só aparecia no <c>E-mission-algo-cs-hanoi</c>
/// (L4), depois de ~30 turnos de GPU e com o FAIL já consumido.
///
/// O corpo é enviado por um HttpClient próprio (não <c>PostObj</c>): com ~400k
/// chars o <c>PostObj</c> publicaria o enunciado inteiro no painel do run, que
/// suporta prompt curto. O transporte HTTP é do consumidor; a REGRA (detectar,
/// nudgar, compactar, 413) mora no Core e no host da bridge.
/// </summary>
internal sealed class E2EContextOverflowTests
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(280) };

    /// <summary>Texto ALTA entropia: ~1.4 chars/token medido no llama — quatro vezes
    /// mais denso que prosa, então 400k chars estouram qualquer slot de 128k.</summary>
    static string Blob(int chars)
    {
        const string abc = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 ,.;:";
        var rnd = new Random(7);                       // seed fixa = run reproduzivel
        var sb = new StringBuilder(chars);
        for (int i = 0; i < chars; i++) sb.Append(abc[rnd.Next(abc.Length)]);
        return sb.ToString();
    }

    static async Task<(int Status, string Body, double Secs)> Post(string url, JsonNode body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("X-Bridge-Mode", TestContext.Mode);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (TestContext.ApiKey ?? ""));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var res = await Http.SendAsync(req);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync(), sw.Elapsed.TotalSeconds);
        }
        catch (TaskCanceledException)
        {
            return (-1, "timeout", sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>Ctx por-slot da bridge alvo (status): 0 quando a bridge nao informa.</summary>
    static async Task<(int Tokens, int Slots)> PerSlot()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, TestContext.Bridge.TrimEnd('/') + "/api/status");
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (TestContext.ApiKey ?? ""));
        try
        {
            using var res = await Http.SendAsync(req);
            var st = JsonNode.Parse(await res.Content.ReadAsStringAsync())?.AsObject();
            int tokens = st?["context_tokens"]?.GetValue<int>() ?? 0;
            int slots = st?["context_slots"]?.GetValue<int>() ?? 1;
            return (tokens, slots > 0 ? slots : 1);
        }
        catch { return (0, 1); }
    }

    /// <summary>400 seco vira 200: a bridge nudga, compacta e o modelo responde.</summary>
    public static async Task OverflowRecovers()
    {
        // Histórico com o BLOB antigo (nao o ultimo turno): e' o unico que o
        // corte pode trocar por marcador e ainda devolver o request ao slot.
        var msgs = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = "voce e um executor." },
            new JsonObject { ["role"] = "user", ["content"] = "tarefa" });
        msgs.Add(new JsonObject
        {
            ["role"] = "assistant", ["content"] = "",
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "c0", ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "write",
                    ["arguments"] = new JsonObject { ["path"] = "big.cs", ["content"] = Blob(430_000) }
                        .ToJsonString()
                }
            })
        });
        msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c0", ["content"] = "ok" });
        for (int i = 0; i < 12; i++)
        {
            msgs.Add(new JsonObject { ["role"] = "assistant", ["content"] = "ok" });
            msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c" + i, ["content"] = "ok" });
        }
        msgs.Add(new JsonObject { ["role"] = "user", ["content"] = "responda apenas OK" });

        var body = new JsonObject
        {
            ["model"] = TestContext.SuiteModel,
            ["messages"] = msgs,
            ["max_tokens"] = 32,
            ["stream"] = false
        };
        Console.WriteLine($"  [ctx-smoke] payload={body.ToJsonString().Length} chars (blob 430k de alta entropia)");
        var (status, resp, secs) = await Post(TestContext.Bridge.TrimEnd('/') + "/v1/chat/completions", body);
        Console.WriteLine($"  [ctx-smoke] recover -> {status} em {secs:F1}s");
        TestContext.Check(status == 200,
            $"estouro de ctx vira 200 apos a escalada (veio {status}: {resp.Replace("\n", " ")[..Math.Min(220, resp.Length)]})");
        var obj = JsonNode.Parse(resp)?.AsObject();
        string content = obj?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(content.Contains("OK", StringComparison.OrdinalIgnoreCase),
            "o modelo respondeu de verdade depois da compactacao (content: " + content[..Math.Min(40, content.Length)] + ")");
        int prompt = obj?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
        var (tokens, slots) = await PerSlot();
        int perSlot = tokens > 0 ? tokens / slots : 0;
        Console.WriteLine($"  [ctx-smoke] prompt_tokens={prompt} por-slot={perSlot}");
        TestContext.Check(prompt == 0 || perSlot == 0 || prompt < perSlot * 0.9,
            $"request reenviada abaixo do teto (prompt={prompt} por-slot={perSlot})");
    }

    /// <summary>Nem a compactação salva -> 413 honesto com o codigo certo.</summary>
    public static async Task OverflowExhausted()
    {
        // Duas mensagens: o corte preserva o "ultimo turno" (só marca args
        // antigos), entao nada encolhe e a bridge nao pode recuperar — e' o caso
        // em que o 413 tem de sair em vez de repassar o 400 cru.
        var msgs = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = "voce e um executor." },
            new JsonObject
            {
                ["role"] = "assistant", ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = "c0", ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "write",
                        ["arguments"] = new JsonObject { ["path"] = "huge.cs", ["content"] = Blob(200_000) }
                            .ToJsonString()
                    }
                })
            });
        msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = "c0", ["content"] = "ok" });
        msgs.Add(new JsonObject { ["role"] = "user", ["content"] = "responda apenas OK" });

        var body = new JsonObject
        {
            ["model"] = TestContext.SuiteModel,
            ["messages"] = msgs,
            ["max_tokens"] = 32,
            ["stream"] = false
        };
        var (status, resp, secs) = await Post(TestContext.Bridge.TrimEnd('/') + "/v1/chat/completions", body);
        Console.WriteLine($"  [ctx-smoke] exhausted -> {status} em {secs:F1}s");
        TestContext.Check(status == 413,
            $"sem como compactar -> 413 (veio {status})");
        TestContext.Check(resp.Contains("context_compact_required", StringComparison.Ordinal),
            "413 carrega o codigo context_compact_required (o cliente sabe o que fazer)");
        TestContext.Check(!resp.Contains("exceed_context_size", StringComparison.Ordinal),
            "o 400 cru do llama nao chega ao cliente");
    }
}
