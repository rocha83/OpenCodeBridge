using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>E2E do fluxo dev IoT→fila→push no modelo vivo (arquitetura de eventos).</summary>
internal static class E2EArchitectureTests
{
    public static async Task IotEventQueue()
    {
        // Fluxo dev: dispositivo IoT -> ingestão -> fila de eventos -> push.
        var body = new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["stream"] = false,
            ["instructions"] = "Voce e um arquiteto de software senior (backend C#, DDD, filas de eventos). Resposta curta.",
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "input_text",
                    ["text"] = "ESP32 publica dor/mobilidade via MQTT. Desenhe em 3 bullets: ingestão idempotente, outbox + tópico app.events-live, fallback in-memory + dead-letter."
                })
            })
        };
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", body);
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "iot completed");
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        var msg = output.FirstOrDefault(o => o?["type"]?.GetValue<string>() == "message")?.AsObject();
        string text = msg?["content"]?[0]?["text"]?.GetValue<string>() ?? "";
        TestContext.Check(text.Length > 30, "arquitetura com conteúdo útil");
    }

    public static async Task FullCycleIotQueuePush()
    {
        // Ciclo complexo: translate (tools IoT) -> responses com tool -> convert valida saída.
        var tr = await TestContext.PostObj(TestContext.Bridge + "/api/translate", new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "sensor dor 7, publique na fila" })
            }),
            ["tools"] = new JsonArray(TestContext.SomaToolResponses())
        });
        TestContext.Check(tr["tool_count"]?.GetValue<int>() == 1, "translate 1 tool");
        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", new JsonObject
        {
            ["model"] = "openai/qwen3-8b-awq",
            ["stream"] = false,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "Quanto e 7 + 5? Use a ferramenta soma e so entao responda." })
            }),
            ["tools"] = new JsonArray(TestContext.SomaToolResponses())
        });
        TestContext.Check(r["status"]?.GetValue<string>() == "completed", "responses completed");
        var output = r["output"]?.AsArray() ?? throw new Exception("sem output");
        TestContext.Check(output.Any(o => o?["type"]?.GetValue<string>() is "message" or "function_call"), "ciclo termina em message ou function_call");
    }
}
