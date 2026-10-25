using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Test;

// Unit dos contadores por anel. O desenho "catalogo sempre cheio, deny so no
// risco" so vale se a recusa for contada: anel 2 (policy de tool/role), anel 3
// (argv barrado no inspetor de shell) e nudge (args guiados antes de executar).
// Sem HTTP e sem modelo; o contador e independente de --diagnostic.
internal static class UnitMetricsTests
{
    public static Task CountersIncrement()
    {
        Metrics.Clear();
        TestContext.Check(Metrics.Ring2Deny == 0 && Metrics.Ring3Deny == 0 && Metrics.NudgeArgs == 0,
            "contadores zerados por Clear");
        Metrics.Deny("ring2", "write", "role");
        Metrics.Deny("ring3", "shell", "token 'rm' negado pelo inspetor");
        Metrics.Deny("nudge", "edit", "faltou newString");
        var snap = Metrics.DenyCounters();
        TestContext.Check(snap["ring2_deny"]!.GetValue<long>() == 1
            && snap["ring3_deny"]!.GetValue<long>() == 1
            && snap["nudge_args"]!.GetValue<long>() == 1,
            "DenyCounters publica ring2/ring3/nudge");
        return Task.CompletedTask;
    }

    public static Task ShellDenyIsRing3()
    {
        Metrics.Clear();
        string dir = Path.Combine("/tmp/opencode", "ocb-ring3-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var exec = new global::Rochas.OpenCodeBridge.Core.Tools.BridgeToolExecutor(dir);
            var mv = exec.Execute("shell", "{\"command\":\"mv a.cs b.cs\"}", 10);
            TestContext.Check(!mv.Success && Metrics.Ring3Deny == 1, "mv negado conta no anel 3");
            var sed = exec.Execute("shell", "{\"command\":\"sed -i 's/a/b/' a.cs\"}", 10);
            TestContext.Check(!sed.Success && Metrics.Ring3Deny == 2, "sed -i negado conta no anel 3");
            var ok = exec.Execute("shell", "{\"command\":\"echo ok\"}", 10);
            TestContext.Check(ok.Success && Metrics.Ring3Deny == 2, "comando liberado nao conta no anel 3");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        return Task.CompletedTask;
    }
}
