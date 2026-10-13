using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

// Bypass Responses->chat SSE: tradução pura, sem HTTP.
internal static class ResponsesBypassTests
{
    static int Failures;
    static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    internal static int Run()
    {
        BuildInputShape();
        OutputTextDelta();
        ReasoningDelta();
        CompletedUsage();
        FailedError();
        IgnoredEvents();
        return Failures;
    }

    static void BuildInputShape()
    {
        var msgs = new JsonArray(
            new JsonObject { ["role"] = "user", ["content"] = "oi" },
            new JsonObject { ["role"] = "assistant", ["content"] = "ola" });
        var body = Services.ResponsesBypass.BuildBody("qwen3.5-9b", 0.6, "seja breve", msgs, 1024);
        Check(body["model"]?.GetValue<string>() == "qwen3.5-9b", "U-bypass-model");
        Check(body["instructions"]?.GetValue<string>() == "seja breve", "U-bypass-instructions");
        Check(body["stream"]?.GetValue<bool>() == true, "U-bypass-stream");
        Check(body["max_tokens"]?.GetValue<int>() == 1024, "U-bypass-maxtokens");
        var input = body["input"]?.AsArray();
        Check(input?.Count == 2, "U-bypass-input-count");
        var first = input?[0]?.AsObject();
        Check(first?["type"]?.GetValue<string>() == "message" && first?["role"]?.GetValue<string>() == "user",
            "U-bypass-input-msg");
        Check(first?["content"]?[0]?["type"]?.GetValue<string>() == "input_text", "U-bypass-input-part");
    }

    static void OutputTextDelta()
    {
        var c = new StringBuilder(); var t = new StringBuilder();
        int? p = null, co = null;
        var r = Services.ResponsesBypass.TranslateData(
            "{\"type\":\"response.output_text.delta\",\"delta\":\"ola\"}", c, t, ref p, ref co);
        Check(!r.Completed && r.Error is null, "U-bypass-delta-flags");
        Check(c.ToString() == "ola", "U-bypass-delta-accum");
        var chat = JsonNode.Parse(r.ChatData!)?.AsObject();
        Check(chat?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>() == "ola", "U-bypass-delta-chat");
    }

    static void ReasoningDelta()
    {
        var c = new StringBuilder(); var t = new StringBuilder();
        int? p = null, co = null;
        var r = Services.ResponsesBypass.TranslateData(
            "{\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"pensando\"}", c, t, ref p, ref co);
        Check(t.ToString() == "pensando" && c.Length == 0, "U-bypass-reason-accum");
        var chat = JsonNode.Parse(r.ChatData!)?.AsObject();
        Check(chat?["choices"]?[0]?["delta"]?["reasoning_content"]?.GetValue<string>() == "pensando",
            "U-bypass-reason-chat");
    }

    static void CompletedUsage()
    {
        var c = new StringBuilder(); var t = new StringBuilder();
        int? p = null, co = null;
        var r = Services.ResponsesBypass.TranslateData(
            "{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":10,\"output_tokens\":3}}}",
            c, t, ref p, ref co);
        Check(r.Completed && p == 10 && co == 3, "U-bypass-completed-usage");
        var chat = JsonNode.Parse(r.ChatData!)?.AsObject();
        Check(chat?["choices"]?[0]?["finish_reason"]?.GetValue<string>() == "stop", "U-bypass-completed-stop");
    }

    static void FailedError()
    {
        var c = new StringBuilder(); var t = new StringBuilder();
        int? p = null, co = null;
        var r = Services.ResponsesBypass.TranslateData(
            "{\"type\":\"response.failed\",\"error\":{\"message\":\"jinja boom\"}}", c, t, ref p, ref co);
        Check(r.Completed && r.Error == "jinja boom" && r.ChatData is null, "U-bypass-failed");
    }

    static void IgnoredEvents()
    {
        var c = new StringBuilder(); var t = new StringBuilder();
        int? p = null, co = null;
        var r1 = Services.ResponsesBypass.TranslateData(
            "{\"type\":\"response.created\",\"response\":{\"id\":\"r1\"}}", c, t, ref p, ref co);
        var r2 = Services.ResponsesBypass.TranslateData("nota-json", c, t, ref p, ref co);
        var r3 = Services.ResponsesBypass.TranslateData("[DONE]", c, t, ref p, ref co);
        Check(r1.ChatData is null && !r1.Completed, "U-bypass-ignore-created");
        Check(r2.ChatData is null && !r2.Completed, "U-bypass-ignore-garbage");
        Check(r3.Completed && r3.ChatData == "[DONE]", "U-bypass-done");
    }
}
