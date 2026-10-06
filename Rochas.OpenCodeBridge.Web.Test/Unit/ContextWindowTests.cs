// Testes unitários de ContextWindow (lógica pura, sem dependências externas).
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130

using System.Collections.Generic;
using System.Linq;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class ContextWindowTests
{
    private static int Failures;

    internal static void Run()
    {
        BuildContext_Basic();
        BuildContext_RespectsTokenLimit();
        BuildContext_SingleMessageOverLimit();
        BuildContext_EmptyHistory();
        BuildContext_ExactTokenLimit();
        BuildContext_EstimateTokens();
        BuildContext_EstimateTotalTokens();

        System.Console.WriteLine($"=== ContextWindow Unit: {7 - Failures}/7 PASS, {Failures} FAIL ===");
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static void BuildContext_Basic()
    {
        var history = new List<SessionMessage>
        {
            new() { Role = "system", Content = "You are helpful." },
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there!" }
        };
        var ctx = ContextWindow.BuildContext(history);
        Check(ctx.Count == 3, "BuildContext_Basic count");
        Check(ctx[0].Role == "system" && ctx[2].Role == "assistant", "BuildContext_Basic order");
    }

    private static void BuildContext_RespectsTokenLimit()
    {
        // ~4 chars/token → 100 chars ≈ 25 tokens
        var history = new List<SessionMessage>();
        for (int i = 0; i < 10; i++)
            history.Add(new SessionMessage { Role = "user", Content = new string('x', 100) });

        var ctx = ContextWindow.BuildContext(history, maxInputTokens: 50); // só cabe ~2 msgs
        Check(ctx.Count <= 3, "BuildContext_RespectsTokenLimit truncates");
        // Deve manter as mais recentes (do fim para o início)
        Check(ctx.All(m => m.Content.Length == 100), "BuildContext_RespectsTokenLimit content intact");
    }

    private static void BuildContext_SingleMessageOverLimit()
    {
        var history = new List<SessionMessage>
        {
            new() { Role = "user", Content = new string('x', 10000) } // ~2500 tokens > limit
        };
        var ctx = ContextWindow.BuildContext(history, maxInputTokens: 100);
        // Mesmo estourando, deve retornar pelo menos a mensagem (comportamento defensivo)
        Check(ctx.Count == 1, "BuildContext_SingleMessageOverLimit returns one");
    }

    private static void BuildContext_EmptyHistory()
    {
        var ctx = ContextWindow.BuildContext(new List<SessionMessage>());
        Check(ctx.Count == 0, "BuildContext_EmptyHistory returns empty");
    }

    private static void BuildContext_ExactTokenLimit()
    {
        // 4 chars/token → 400 chars = 100 tokens exatos
        var history = new List<SessionMessage>
        {
            new() { Role = "user", Content = new string('a', 400) }
        };
        var ctx = ContextWindow.BuildContext(history, maxInputTokens: 100);
        Check(ctx.Count == 1, "BuildContext_ExactTokenLimit includes exact fit");
    }

    private static void BuildContext_EstimateTokens()
    {
        Check(ContextWindow.EstimateTokens("") == 1, "EstimateTokens empty = 1");
        Check(ContextWindow.EstimateTokens("abcd") == 1, "EstimateTokens 4 chars = 1");
        Check(ContextWindow.EstimateTokens(new string('x', 100)) == 25, "EstimateTokens 100 chars = 25");
        Check(ContextWindow.EstimateTokens(new string('x', 101)) == 26, "EstimateTokens 101 chars = 26 (ceil)");
    }

    private static void BuildContext_EstimateTotalTokens()
    {
        var history = new List<SessionMessage>
        {
            new() { Content = new string('x', 100) }, // 25
            new() { Content = new string('y', 200) }  // 50
        };
        var total = ContextWindow.EstimateTotalTokens(history);
        Check(total == 75, "EstimateTotalTokens sums correctly");
    }
}