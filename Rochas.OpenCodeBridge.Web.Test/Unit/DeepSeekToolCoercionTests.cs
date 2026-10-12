// Testes unitários da coerção DeepSeek (parse de tool calls em texto).
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --units-only

using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class DeepSeekToolCoercionTests
{
    private static int Failures;
    private const string DsModel = "deepseek-coder-v2-lite-instruct";

    internal static int Run()
    {
        HermesSingle();
        HermesMultiple();
        HermesArgsString();
        HermesInvalidSkipped();
        JsonFence();
        JsonFenceNonObjectIgnored();
        V3Tokens();
        PlainTextUntouched();
        NonDeepSeekUntouched();
        EmptyInputs();
        BadNameSkipped();
        ChatTurnCoerces();
        ChatTurnKeepsStructured();
        XmlSingle();
        XmlMulti();
        XmlUnknownSkipped();
        XmlEntities();
        AttemptCompletion();
        ExecutorWrite();
        ExecutorShellDenied();
        ExecutorPassthrough();
        ExecutorNudgesThenCalls();
        ExecutorStopsAfterWork();
        FenceSingleKey();

        System.Console.WriteLine($"=== DeepSeekCoercion Unit: {24 - Failures}/24 PASS, {Failures} FAIL ===");
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static void HermesSingle()
    {
        string content = "Vou escrever o script.\n<tool_call>{\"name\": \"write\", \"arguments\": {\"path\": \"fase1.sh\", \"content\": \"echo oi\"}}</tool_call>\npronto.";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned);
        Check(calls.Count == 1 && calls[0].Name == "write", "U-dsq-hermes-single");
        Check(cleaned.Contains("Vou escrever") && !cleaned.Contains("<tool_call>"), "U-dsq-hermes-cleaned");
        Check(calls.Count == 1 && calls[0].Args.Contains("fase1.sh"), "U-dsq-hermes-args");
    }

    private static void HermesMultiple()
    {
        string content = "<tool_call>{\"name\":\"shell\",\"arguments\":{\"command\":\"bash -n a.sh\"}}</tool_call>\n<tool_call>{\"name\":\"read\",\"arguments\":{\"path\":\"x.cs\"}}</tool_call>";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out _);
        Check(calls.Count == 2 && calls[0].Name == "shell" && calls[1].Name == "read", "U-dsq-hermes-multi");
        Check(calls[0].Id != calls[1].Id, "U-dsq-hermes-distinct-ids");
    }

    private static void HermesArgsString()
    {
        string content = "<tool_call>{\"name\": \"shell\", \"arguments\": \"{\\\"command\\\": \\\"ls\\\"}\"}</tool_call>";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out _);
        Check(calls.Count == 1 && calls[0].Args.Contains("ls"), "U-dsq-hermes-args-string");
    }

    private static void HermesInvalidSkipped()
    {
        string content = "texto <tool_call>{nao json}</tool_call> fim";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned);
        Check(calls.Count == 0 && cleaned == content, "U-dsq-hermes-invalid-skipped");
    }

    private static void JsonFence()
    {
        string content = "chamando:\n```json\n{\"name\": \"write\", \"arguments\": {\"path\": \"a.sh\", \"content\": \"x\"}}\n```\nfim";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned);
        Check(calls.Count == 1 && calls[0].Name == "write", "U-dsq-fence-single");
        Check(!cleaned.Contains("```"), "U-dsq-fence-cleaned");
    }

    private static void JsonFenceNonObjectIgnored()
    {
        string content = "exemplo:\n```sh\necho oi\n```\nfim";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned);
        Check(calls.Count == 0 && cleaned == content, "U-dsq-fence-sh-ignored");
    }

    private static void V3Tokens()
    {
        string content = "ok <｜tool▁calls▁begin｜><｜tool▁call▁begin｜>function<｜tool▁sep｜>shell\n```json\n{\"command\": \"ls\"}\n```<｜tool▁call▁end｜><｜tool▁calls▁end｜> fim";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned);
        Check(calls.Count == 1 && calls[0].Name == "shell" && calls[0].Args.Contains("ls"), "U-dsq-v3-tokens");
        Check(!cleaned.Contains("tool▁calls"), "U-dsq-v3-cleaned");
    }

    private static void PlainTextUntouched()
    {
        string content = "Só texto, sem chamadas.";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned);
        Check(calls.Count == 0 && cleaned == content, "U-dsq-plain-untouched");
    }

    private static void NonDeepSeekUntouched()
    {
        string content = "<tool_call>{\"name\": \"write\", \"arguments\": {}}</tool_call>";
        var calls = DeepSeekToolCoercion.Coerce("qwen3-8b-awq", content, out string cleaned);
        Check(calls.Count == 0 && cleaned == content, "U-dsq-gated-by-model");
    }

    private static void EmptyInputs()
    {
        var a = DeepSeekToolCoercion.Coerce(DsModel, "", out _);
        var b = DeepSeekToolCoercion.Coerce(null, "x", out _);
        Check(a.Count == 0 && b.Count == 0, "U-dsq-empty");
    }

    private static void BadNameSkipped()
    {
        string content = "<tool_call>{\"name\": \"rm -rf /\", \"arguments\": {}}</tool_call>";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out _);
        Check(calls.Count == 0, "U-dsq-bad-name-skipped");
    }

    // Integração (sem GPU): turno com SSE de texto deepseek vira Calls.
    private static void ChatTurnCoerces()
    {
        string inner = "{\"name\": \"shell\", \"arguments\": {\"command\": \"bash -n f.sh\"}}";
        string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"texto <tool_call>" +
            inner.Replace("\\", "\\\\").Replace("\"", "\\\"") +
            "</tool_call>\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var stub = new SseStub(sse);
        var tools = new JsonArray { new JsonObject() };
        var t = BridgeHelper.ChatTurnAsync(stub, "http://x", DsModel, 0.4, "sys",
            new JsonArray(), tools, CancellationToken.None).GetAwaiter().GetResult();
        Check(t.Ok && t.Calls.Count == 1 && t.Calls[0].Name == "shell", "U-dsq-turn-coerces");
        Check(!t.Content.Contains("<tool_call>"), "U-dsq-turn-cleaned");
    }

    // Regressão: tool_calls estruturadas continuam valendo (sem coerção).
    private static void ChatTurnKeepsStructured()
    {
        string sse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"read\",\"arguments\":\"{}\"}}],\"content\":\"\"},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
        var stub = new SseStub(sse);
        var tools = new JsonArray { new JsonObject() };
        var t = BridgeHelper.ChatTurnAsync(stub, "http://x", DsModel, 0.4, "sys",
            new JsonArray(), tools, CancellationToken.None).GetAwaiter().GetResult();
        Check(t.Ok && t.Calls.Count == 1 && t.Calls[0].Id == "c1", "U-dsq-turn-structured-kept");
    }

    private static readonly List<string> Known = new() { "write", "edit", "shell", "subagent", "read" };

    private static void XmlSingle()
    {
        string content = "Escrevendo.\n<write><path>f.sh</path><content>echo oi</content></write>\nfim.";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned, Known);
        Check(calls.Count == 1 && calls[0].Name == "write" && calls[0].Args.Contains("f.sh"), "U-dsq-xml-single");
        Check(!cleaned.Contains("<write>") && cleaned.Contains("Escrevendo"), "U-dsq-xml-cleaned");
    }

    private static void XmlMulti()
    {
        string content = "<shell><command>bash -n f.sh</command></shell>\n<read><path>x.cs</path></read>";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out _, Known);
        Check(calls.Count == 2 && calls[0].Name == "shell" && calls[1].Name == "read", "U-dsq-xml-multi");
    }

    private static void XmlUnknownSkipped()
    {
        string content = "texto <deletar><x>1</x></deletar> fim";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned, Known);
        Check(calls.Count == 0 && cleaned == content, "U-dsq-xml-unknown-skipped");
    }

    private static void XmlEntities()
    {
        string content = "<write><path>a &amp; b.sh</path><content>x &lt;y&gt;</content></write>";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out _, Known);
        Check(calls.Count == 1 && calls[0].Args.Contains("a & b.sh") && calls[0].Args.Contains("x <y>"), "U-dsq-xml-entities");
    }

    private static void AttemptCompletion()
    {
        string content = "ruído <attempt_completion><result>feito</result></attempt_completion> cauda";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned, Known);
        Check(calls.Count == 0 && cleaned.Contains("feito") && !cleaned.Contains("<attempt_completion>"), "U-dsq-attempt-completion");
    }

    private static void FenceSingleKey()
    {
        string content = "```json\n{\n  \"subagent\": {\n    \"prompt\": \"Fase 1.\"\n  }\n}\n```";
        var calls = DeepSeekToolCoercion.Coerce(DsModel, content, out string cleaned, Known);
        Check(calls.Count == 1 && calls[0].Name == "subagent" && calls[0].Args.Contains("Fase 1"), "U-dsq-fence-single-key");
        Check(!cleaned.Contains("```"), "U-dsq-fence-single-key-cleaned");
    }

    private static string SseText(string content)
    {
        string e = content.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");
        return "data: {\"choices\":[{\"delta\":{\"content\":\"" + e + "\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
    }

    private static void ExecutorWrite()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dsq-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var inner = new ToolExecutor(ToolExecutor.DefaultHandlers(), root, Path.Combine(root, "audit.log"));
            var dsq = new DeepSeekToolExecutor(inner, _ => true);
            var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "faca" } };
            var stub = new SseStub(new[]
            {
                SseText("<write><path>f.sh</path><content>echo oi</content></write>"),
                SseText("pronto"),
            });
            var r = dsq.RunAsync(stub, "http://x", DsModel, 0.4, "sys", history,
                new List<string> { "write", "edit", "shell", "subagent" }, 5, null,
                CancellationToken.None).GetAwaiter().GetResult();
            Check(r.Ok && r.Text.Contains("[write] ok"), "U-dsq-exec-write-ok");
            Check(System.IO.File.Exists(Path.Combine(root, "f.sh")), "U-dsq-exec-write-file");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void ExecutorShellDenied()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dsq-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var inner = new ToolExecutor(ToolExecutor.DefaultHandlers(), root, Path.Combine(root, "audit.log"));
            var dsq = new DeepSeekToolExecutor(inner, _ => false);
            var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "faca" } };
            var stub = new SseStub(new[] { SseText("<shell><command>date</command></shell>"), SseText("fim") });
            var r = dsq.RunAsync(stub, "http://x", DsModel, 0.4, "sys", history,
                new List<string> { "write", "edit", "shell", "subagent" }, 5, null,
                CancellationToken.None).GetAwaiter().GetResult();
            Check(r.Ok && r.Text.Contains("FALHOU") && r.Text.Contains("negado"), "U-dsq-exec-shell-denied");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void ExecutorPassthrough()
    {
        var inner = new ToolExecutor(ToolExecutor.DefaultHandlers(), Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "dsq-audit.log"));
        var dsq = new DeepSeekToolExecutor(inner, _ => true);
        var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "faca" } };
        var stub = new SseStub(SseText("só texto, sem chamadas"));
        var r = dsq.RunAsync(stub, "http://x", DsModel, 0.4, "sys", history,
            new List<string> { "write", "edit", "shell", "subagent" }, 7, null,
            CancellationToken.None).GetAwaiter().GetResult();
        // Prosa sem trabalho: 2 cutucadas e para (1 + 2x2 msgs no histórico).
        Check(r.Ok && r.Text.Contains("só texto") && history.Count == 5, "U-dsq-exec-nudge-stop");
    }

    private static void ExecutorStopsAfterWork()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dsq-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var inner = new ToolExecutor(ToolExecutor.DefaultHandlers(), root, Path.Combine(root, "audit.log"));
            var dsq = new DeepSeekToolExecutor(inner, _ => true);
            var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "faca" } };
            var stub = new SseStub(new[]
            {
                SseText("<write><path>h.sh</path><content>echo h</content></write>"),
                SseText("script criado e validado"),
            });
            var r = dsq.RunAsync(stub, "http://x", DsModel, 0.4, "sys", history,
                new List<string> { "write", "edit", "shell", "subagent" }, 7, null,
                CancellationToken.None).GetAwaiter().GetResult();
            bool nudged = history.Any(m => (m as JsonObject)?["content"]?.GetValue<string>()?.Contains("DeepSeek-tools") == true);
            Check(r.Ok && !nudged && System.IO.File.Exists(Path.Combine(root, "h.sh")), "U-dsq-exec-no-nudge-after-work");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static void ExecutorNudgesThenCalls()
    {
        string root = Path.Combine(Path.GetTempPath(), $"dsq-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var inner = new ToolExecutor(ToolExecutor.DefaultHandlers(), root, Path.Combine(root, "audit.log"));
            var dsq = new DeepSeekToolExecutor(inner, _ => true);
            var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "faca" } };
            var stub = new SseStub(new[]
            {
                SseText("vou pensar em prosa primeiro"),
                SseText("mais prosa, sem chamar nada"),
                SseText("<write><path>g.sh</path><content>echo g</content></write>"),
                SseText("fim"),
            });
            var r = dsq.RunAsync(stub, "http://x", DsModel, 0.4, "sys", history,
                new List<string> { "write", "edit", "shell", "subagent" }, 7, null,
                CancellationToken.None).GetAwaiter().GetResult();
            bool nudged = history.Any(m => (m as JsonObject)?["content"]?.GetValue<string>()?.Contains("DeepSeek-tools") == true);
            Check(r.Ok && nudged && System.IO.File.Exists(Path.Combine(root, "g.sh")), "U-dsq-exec-nudge-then-call");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class SseStub : IBridgeClient
    {
        private readonly Queue<string> _q;
        public SseStub(string sse) { _q = new Queue<string>(new[] { sse }); }
        public SseStub(IEnumerable<string> sses) { _q = new Queue<string>(sses); }
        public Task<(bool ok, string error)> StreamAsync(string bridgeUrl, string model, double temperature,
            string systemPrompt, JsonArray messages, Stream output, CancellationToken ct,
            bool includeTools = true, JsonArray? tools = null, int? maxTokens = null, bool? enableThinking = null,
            JsonNode? toolChoice = null)
        {
            string s = _q.Count > 1 ? _q.Dequeue() : _q.Peek();
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            output.Write(bytes, 0, bytes.Length);
            return Task.FromResult((true, ""));
        }
    }
}
