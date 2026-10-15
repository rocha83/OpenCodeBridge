// Simula o delírio ao iniciar sessão: o modelo repete boa-noite/encerrando e
// só sossega após o ping-pong corretivo. Execução:
// dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --units-only
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class CalibrationProbeTests
{
    private static int Failures;
    internal static int Run()
    {
        CedeAposCorretivo().GetAwaiter().GetResult();
        DesisteSeNuncaCeder().GetAwaiter().GetResult();
        ContrasteChatterAbortaDelirio();
        EquilibrioAposPrimeiroDone().GetAwaiter().GetResult();
        RecaiNoReforcoNaoEquilibra().GetAwaiter().GetResult();
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    // Modelo delira 2 turnos e cede no 3º após "não repita...".
    private static async Task CedeAposCorretivo()
    {
        var probe = new CalibrationProbe();
        int calls = 0;
        var seenPrompts = new List<string>();
        var verdict = await probe.CalibrateAsync((prompt, ct) =>
        {
            calls++;
            seenPrompts.Add(prompt);
            if (calls <= 2)
                return Task.FromResult((calls == 1 ? "Boa noite! Como posso ajudar você hoje?" : "Entendido. Encerrarei agora em silêncio como solicitado.", 0));
            return Task.FromResult(("ok", 0));
        });
        Check(verdict.Ceded && verdict.Attempts == 3, "U-calib-cede-3");
        Check(seenPrompts.Count == 3 && seenPrompts[1].Contains("não repita"), "U-calib-corretivo-invisivel");
        Check(verdict.Correctives.Count == 2, "U-calib-2-fixes");
    }

    // Modelo nunca cede: veredito negativo após o teto, sem loop infinito.
    private static async Task DesisteSeNuncaCeder()
    {
        var probe = new CalibrationProbe(maxAttempts: 4);
        var verdict = await probe.CalibrateAsync((prompt, ct) =>
            Task.FromResult(("Boa noite! Como posso ajudar você hoje?", 0)));
        Check(!verdict.Ceded && verdict.Attempts == 4, "U-calib-desiste-4");
        Check(verdict.Correctives.Count == 4, "U-calib-4-fixes");
    }

    // Sanidade: o mesmo delírio que a sonda cura faria o ChatterGuard abortar.
    private static void ContrasteChatterAbortaDelirio()
    {
        var g = new ChatterGuard();
        g.Note("Boa noite! Como posso ajudar você hoje?", 0);
        bool abort = g.Note("Entendido. Encerrarei agora em silêncio como solicitado.", 0);
        // 2 curtos distintos ainda não abortam (teto 3 ou igual 2x); o eco igual sim.
        var g2 = new ChatterGuard();
        g2.Note("Entendido. Encerrarei agora em silêncio como solicitado.", 0);
        bool abortEcho = g2.Note("Entendido. Encerrarei agora em silêncio como solicitado.", 0);
        Check(abortEcho, "U-calib-chatter-eco-aborta");
    }

    // Após o primeiro done, aumenta o teste com reforço e busca equilíbrio.
    private static async Task EquilibrioAposPrimeiroDone()
    {
        var probe = new CalibrationProbe();
        int calls = 0;
        var seen = new List<string>();
        var v = await probe.CalibrateToEquilibriumAsync((prompt, ct) =>
        {
            calls++;
            seen.Add(prompt);
            return Task.FromResult(calls switch
            {
                1 => ("Boa noite! Como posso ajudar você hoje?", 0),
                2 => ("Entendido. Encerrarei agora em silêncio como solicitado.", 0),
                3 => ("ok", 0),
                4 => ("ok-1", 0),
                _ => ("ok-2", 0),
            });
        });
        Check(v.Ceded && v.Balanced, "U-calib-equilibrio-ok");
        Check(seen.Any(p => p.Contains("ritmo correto")) && seen.Any(p => p.Contains("melhorando")), "U-calib-reforco-ritmo");
        Check(v.Attempts == 5, "U-calib-5-passos");
    }

    // Recai no reforço: cedeu mas não equilibrou.
    private static async Task RecaiNoReforcoNaoEquilibra()
    {
        var probe = new CalibrationProbe();
        int calls = 0;
        var v = await probe.CalibrateToEquilibriumAsync((prompt, ct) =>
        {
            calls++;
            if (calls <= 2) return Task.FromResult(("Boa noite! Como posso ajudar você hoje?", 0));
            if (calls == 3) return Task.FromResult(("ok", 0));
            return Task.FromResult(("Boa noite! Como posso ajudar você hoje?", 0));
        });
        Check(v.Ceded && !v.Balanced, "U-calib-recai-sem-equilibrio");
    }
}
