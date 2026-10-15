// Varredura dos 10 enunciados AO VIVO (modelo real :4124, sem mock): após a
// calibragem básica, cada tema deve voltar substantivo e sem delírio.
// Roda com: dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --topics-live
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Test.Integration;

internal static class TopicsLiveTests
{
    // Mesmos 10 temas do TopicsSweepTests (unit com fake).
    internal static readonly string[] Topics =
    [
        "resuma a copa do mundo de 1958.",
        "dê uma receita de risoto fino para acompanhar vinho branco.",
        "resuma as missões Apollo à Lua.",
        "cite 2 mistérios esotéricos sobre a Mesopotâmia.",
        "descreva um protótipo pequeno de app só-leitura de log em ASP.NET MVC com clean DDD, repositório genérico e unit-of-work usando ORM.",
        "cite 2 problemas psiquiátricos comuns na 3ª idade.",
        "como preparar motores 1.8 nacionais no Brasil até 250cv?",
        "cite 2 locais mais raros dentro da floresta amazônica.",
        "cite a medicina natural peruana utilizando DMT.",
        "explique a teoria das cordas e testes de intenções em física quântica.",
    ];

    public static async Task<int> RunAsync(string bridge = "http://127.0.0.1:4124")
    {
        int failures = 0;
        void Check(bool ok, string name, string extra = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (extra == "" ? "" : " :: " + extra));
            if (!ok) failures++;
        }
        var history = new List<(string Role, string Content)>
        {
            ("user", "não repita. Responda só com ok."),
            ("assistant", "ok"),
            ("user", "muito bem, você sossegou e está melhorando. Esse é o ritmo correto, busque o equilíbrio e responda só com ok-2."),
            ("assistant", "ok-2"),
        };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        var decodeRates = new List<string>();
        async Task<string> Chat(string prompt, CancellationToken ct)
        {
            history.Add(("user", prompt));
            var messages = new JsonArray();
            foreach (var (role, content) in history)
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            var body = new JsonObject
            {
                ["model"] = "qwen3.5-9b",
                ["messages"] = messages,
                ["temperature"] = 0.4,
                ["max_tokens"] = 256,
                ["stream"] = false,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            };
            using var res = await http.PostAsync(bridge + "/v1/chat/completions",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            res.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
            string text = json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            string rate = BridgeTps.Read(json);
            if (rate != "") decodeRates.Add(rate);
            history.Add(("assistant", text));
            return text;
        }
        var ct = CancellationToken.None;
        for (int i = 0; i < Topics.Length; i++)
        {
            string answer = "";
            try { answer = await Chat(Topics[i], ct); }
            catch (Exception ex) { Check(false, $"live-topic-{i + 1:00}", ex.GetType().Name); continue; }
            string t = (answer ?? "").Trim();
            bool healthy = t.Length > 40
                && !t.Contains("Como posso ajudar", StringComparison.OrdinalIgnoreCase)
                && !t.Contains("Encerrarei agora em silêncio", StringComparison.OrdinalIgnoreCase)
                && !t.Contains("Encerrando a conversa", StringComparison.OrdinalIgnoreCase);
            Check(healthy, $"live-topic-{i + 1:00}", $"{decodeRates.LastOrDefault()} :: " + (t.Length > 120 ? t[..120].Replace("\n", " ") + "…" : t.Replace("\n", " ")));
        }
        string avg = decodeRates.Count > 0 ? decodeRates.GroupBy(r => r).OrderByDescending(g => g.Count()).First().Key : "";
        Console.WriteLine($"[topics-live] 10 temas, decode {avg} (por resposta no PASS acima)");
        return failures;
    }
}
