// Testes do anti-repeat centralizado no Core e consumido pela Web UI.
// (antes vivia em Web.Services.RepeatLoop; agora a regra e uma so no Core)
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class AntiRepeatCoreTests
{
    private static int Failures;

    internal static int Run()
    {
        VerbatimTail();
        ShortLoop();
        CleanText();
        NoticeAndWindow();
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static void VerbatimTail()
    {
        string loop = string.Concat(Enumerable.Repeat("repete isso ", 20));
        Check(ContentAntiRepeat.TailRepeats(loop, ContentAntiRepeat.DefaultWindow),
            "U-antirepeat-verbatim");
    }

    private static void ShortLoop()
    {
        string loop = string.Concat(Enumerable.Repeat("repete isso agora mesmo!", 4));
        Check(ContentAntiRepeat.TailRepeats(loop, ContentAntiRepeat.DefaultWindow),
            "U-antirepeat-curto");
    }

    private static void CleanText()
    {
        Check(!ContentAntiRepeat.TailRepeats("texto unico, sem repeticao alguma aqui",
                ContentAntiRepeat.DefaultWindow), "U-antirepeat-texto-novo");
    }

    private static void NoticeAndWindow()
    {
        Check(ContentAntiRepeat.DefaultWindow == 128, "U-antirepeat-janela-128");
        Check(ContentAntiRepeat.Notice.Contains("contencao", StringComparison.OrdinalIgnoreCase)
              && ContentAntiRepeat.Notice.Contains("repeticao", StringComparison.OrdinalIgnoreCase),
            "U-antirepeat-aviso");
    }
}
