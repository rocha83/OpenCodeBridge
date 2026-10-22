// Ping-pong psicólogo AO VIVO (modelo real :4124, sem mock): prova que o
// delírio inicial cede após o corretivo e sustenta o equilíbrio com reforço.
// Roda com: dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --calib-live
using System.Text;
using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Integration;

internal static class CalibrationLiveTests
{
    public static async Task<int> RunAsync(string bridge = "http://127.0.0.1:4124")
    {
        int failures = 0;
        void Check(bool ok, string name, string extra = "")
        {
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (extra == "" ? "" : " :: " + extra));
            if (!ok) failures++;
        }
        var history = new List<(string Role, string Content)>();
        var decodeRates = new List<string>();
        async Task<(string Text, int Tools)> Chat(string prompt, CancellationToken ct)
        {
            history.Add(("user", prompt));
            var messages = new JsonArray();
            foreach (var (role, content) in history)
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            var body = new JsonObject
            {
                ["model"] = "qwen3.5-9b",
                ["messages"] = messages,
                ["temperature"] = 0.1,
                ["max_tokens"] = 256,
                ["stream"] = false,
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            };
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            using var res = await http.PostAsync(bridge + "/v1/chat/completions",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            res.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
            string text = json["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            string rate = BridgeTps.Read(json);
            if (rate != "") decodeRates.Add(rate);
            history.Add(("assistant", text));
            return (text, 0);
        }
        // Gatilho: começa como o usuário ("boa noite") para ver o delírio real.
        history.Add(("user", "boa noite"));
        var probe = new CalibrationProbe();
        var verdict = await probe.CalibrateToEquilibriumAsync(async (prompt, ct) =>
        {
            if (prompt == "ping")
                return await Chat("boa noite (repita a saudação curta)", ct);
            return await Chat(prompt, ct);
        });
        Check(verdict.Ceded, "live-calib-ceded", $"attempts={verdict.Attempts} {decodeRates.LastOrDefault()} final={verdict.FinalText}");
        Check(verdict.Balanced, "live-calib-balanced", string.Join(" | ", verdict.Steps.Select(s => s.Length > 60 ? s[..60] + "…" : s)));
        return failures;
    }
}
