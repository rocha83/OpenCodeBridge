using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>E2E de stream SSE e review de arquiteto no modelo vivo.</summary>
internal static class E2EStreamTests
{
    public static async Task ArchitectStream()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["stream"] = true,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Como arquiteto senior: 1 frase sobre quando usar fila em vez de chamada direta." })
            })
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, TestContext.Bridge + "/v1/responses")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var res = await TestContext.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        string text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"stream -> {(int)res.StatusCode}: {text[..Math.Min(200, text.Length)]}");
        TestContext.Check((res.Content.Headers.ContentType?.MediaType ?? "") == "text/event-stream", "content-type sse");
        string? curEvent = null, lastType = null;
        int lastSeq = -1, events = 0;
        foreach (string line in text.Split('\n'))
        {
            if (line.StartsWith("event:")) curEvent = line[6..].Trim();
            else if (line.StartsWith("data:") && curEvent is not null)
            {
                var payload = JsonNode.Parse(line[5..].Trim())?.AsObject();
                int seq = payload?["sequence_number"]?.GetValue<int>() ?? -1;
                TestContext.Check(seq >= lastSeq, $"sequence monotônico ({lastSeq}->{seq})");
                lastSeq = seq; lastType = curEvent; events++; curEvent = null;
                if (lastType == "response.completed") break;
            }
        }
        TestContext.Check(events > 3, "eventos suficientes");
        TestContext.Check(lastType == "response.completed", "termina em response.completed");
    }

    public static async Task ArchitectReview()
    {
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["stream"] = false,
            ["instructions"] = "Voce e um arquiteto de software senior fazendo code review. Seja direto: 1 risco principal.",
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = "Revise este diff:\n```diff\n- var logDir = Path.GetDirectoryName(LogPath);\n+ var logDir = Path.GetDirectoryName(LogPath)!;\n```\nQual o risco?"
                })
            }),
            ["tools"] = new JsonArray(TestContext.SomaToolResponses())
        };
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", body);
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "review completed");
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        if (TestContext.WantsReasoning) TestContext.Check(output.Any(o => o?["type"]?.GetValue<string>() == "reasoning"), "review tem reasoning");
        var msg = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "message")?.AsObject();
        string text = msg?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        bool hasCall = output.Any(o => o?["type"]?.GetValue<string>() == "function_call");
        TestContext.Check(text.Length > 20 || (TestContext.ServedIsCoder && hasCall), "review tem texto util");
    }
}
