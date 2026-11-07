using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Leitor do formato canonico de tool call do Gemma 3/4 (template nativo,
/// "fix july 2026"): <c>&lt;|tool_call&gt;call:nome{chave:&lt;|"|&gt;valor&lt;|"|&gt;}&lt;tool_call|&gt;</c>.
/// Sem ele o bloco chega ao cliente como texto e a ordem vira prosa — medido em
/// 2026-11-07 com o GGUF Q4_K_M-Imatrix (2 respostas vazaram o markup cru).
/// </summary>
internal static class UnitGemmaToolCallsTests
{
    public static Task Run()
    {
        Simples();
        Tipos();
        Multiplas();
        StripDeMarkup();
        WiringNaCoercao();
        CoercaoChatLimpa();
        ProsaSemCall();
        return Task.CompletedTask;
    }

    static void Simples()
    {
        const string s = "<|tool_call>call:write{content:<|\"|>oi<|\"|>,path:<|\"|>desktop/project/index.html<|\"|>}<tool_call|>";
        var calls = GemmaToolCalls.Extract(s);
        TestContext.Check(calls.Count == 1, "gemma: uma call extraida");
        TestContext.Check(calls[0].Name == "write", "gemma: nome=write");
        TestContext.Check(S(calls[0].Args, "content") == "oi", "gemma: content string");
        TestContext.Check(S(calls[0].Args, "path") == "desktop/project/index.html", "gemma: path preservado");
    }

    static void Tipos()
    {
        const string s = "<|tool_call>call:shell{command:<|\"|>dotnet build<|\"|>,timeout:30,forced:true,nada:null,opts:{cwd:<|\"|>/tmp/x<|\"|>,tags:[<|\"|>a<|\"|>,<|\"|>b<|\"|>]}}<tool_call|>";
        var calls = GemmaToolCalls.Extract(s);
        TestContext.Check(calls.Count == 1, "gemma tipos: uma call");
        var a = calls[0].Args;
        TestContext.Check(S(a, "command") == "dotnet build", "gemma tipos: string");
        TestContext.Check(a["timeout"]?.GetValue<long>() == 30, "gemma tipos: inteiro");
        TestContext.Check(a["forced"]?.GetValue<bool>() == true, "gemma tipos: booleano");
        TestContext.Check(a["nada"] is null, "gemma tipos: null");
        TestContext.Check(S(a["opts"]!.AsObject(), "cwd") == "/tmp/x", "gemma tipos: objeto aninhado");
        TestContext.Check(a["opts"]!["tags"]!.AsArray().Count == 2, "gemma tipos: array");
    }

    static void Multiplas()
    {
        const string s = "<|tool_call>call:git{command:<|\"|>init<|\"|>}<tool_call|>" +
                         "<|tool_call>call:shell{command:<|\"|>mkdir -p dados<|\"|>}<tool_call|>";
        var calls = GemmaToolCalls.Extract(s);
        TestContext.Check(calls.Count == 2, "gemma multi: duas calls");
        TestContext.Check(calls[0].Name == "git" && calls[1].Name == "shell", "gemma multi: ordem preservada");
    }

    static void StripDeMarkup()
    {
        const string s = "vou iniciar o repo\n<|tool_call>call:git{command:<|\"|>init<|\"|>}<tool_call|>";
        string clean = GemmaToolCalls.Strip(s);
        TestContext.Check(!clean.Contains("tool_call"), "gemma strip: markup removido");
        TestContext.Check(clean.Contains("vou iniciar o repo"), "gemma strip: prosa preservada");
    }

    static void WiringNaCoercao()
    {
        const string s = "<|tool_call>call:write{content:<|\"|>a<|\"|>,path:<|\"|>a.txt<|\"|>}<tool_call|>";
        var known = new List<string> { "write", "shell" };
        var calls = ToolCoercion.ExtractTextToolCalls(s, known);
        TestContext.Check(calls.Count == 1, "gemma coerce: call detectada no texto");
        TestContext.Check(calls.Count > 0 && calls[0].Name == "write", "gemma coerce: nome=write");
        TestContext.Check(calls.Count > 0 && calls[0].Arguments.Contains("\"path\""), "gemma coerce: arguments JSON");
        TestContext.Check(ToolCoercion.TextToolMarkerIndex(s) == 0, "gemma coerce: marcador <|tool_call> reconhecido");
    }

    static void CoercaoChatLimpa()
    {
        var root = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "vou criar o arquivo\n<|tool_call>call:write{content:<|\"|>a<|\"|>,path:<|\"|>a.txt<|\"|>}<tool_call|>"
                },
                ["finish_reason"] = "stop"
            })
        };
        int n = ToolCoercion.CoerceChatCalls(root, new List<string> { "write" });
        var msg = root["choices"]![0]!["message"]!.AsObject();
        TestContext.Check(n == 1, "gemma chat: uma call coagida");
        TestContext.Check(msg["tool_calls"] is JsonArray { Count: 1 }, "gemma chat: tool_calls no message");
        TestContext.Check(!(msg["content"]?.GetValue<string>() ?? "").Contains("tool_call"),
            "gemma chat: markup removido do content");
        TestContext.Check(root["choices"]![0]!["finish_reason"]?.GetValue<string>() == "tool_calls",
            "gemma chat: finish_reason=tool_calls");
    }

    static void ProsaSemCall()
    {
        var calls = GemmaToolCalls.Extract("Sim, o git serve para versionar arquivos.");
        TestContext.Check(calls.Count == 0, "gemma: prosa pura nao vira call");
    }

    static string S(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }
}
