// Testes unitários do corta-espiral de prosa (loop boa-noite/encerrando).
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --units-only

using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class ChatterGuardTests
{
    private static int Failures;

    internal static int Run()
    {
        ThreeShortsStops();
        SameTwiceStops();
        ToolResets();
        LongResets();
        MixedResets();
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static void ThreeShortsStops()
    {
        var g = new ChatterGuard();
        Check(!g.Note("Boa noite! Como posso ajudar?", 0), "U-chatter-1");
        Check(!g.Note("Claro, estou aqui!", 0), "U-chatter-2");
        Check(g.Note("Pois não?", 0), "U-chatter-3-stop");
    }

    private static void SameTwiceStops()
    {
        var g = new ChatterGuard();
        Check(!g.Note("Encerrando a conversa.", 0), "U-chatter-same-1");
        Check(g.Note("Encerrando a conversa.", 0), "U-chatter-same-2-stop");
    }

    private static void ToolResets()
    {
        var g = new ChatterGuard();
        Check(!g.Note("curta 1", 0), "U-chatter-tool-1");
        Check(!g.Note("curta 2", 0), "U-chatter-tool-2");
        Check(!g.Note("", 1), "U-chatter-tool-reset");
        Check(!g.Note("curta 3", 0), "U-chatter-tool-3");
        Check(!g.Note("curta 4", 0), "U-chatter-tool-4");
        Check(g.Note("curta 5", 0), "U-chatter-tool-5-stop");
    }

    private static void LongResets()
    {
        var g = new ChatterGuard();
        Check(!g.Note("curta 1", 0), "U-chatter-long-1");
        Check(!g.Note(new string('x', 200), 0), "U-chatter-long-reset");
        Check(!g.Note("curta 2", 0), "U-chatter-long-2");
    }

    private static void MixedResets()
    {
        var g = new ChatterGuard();
        g.Note("a", 0);
        g.Reset();
        Check(!g.Note("b", 0), "U-chatter-reset-1");
        Check(g.Note("b", 0), "U-chatter-reset-2-stop");
    }
}
