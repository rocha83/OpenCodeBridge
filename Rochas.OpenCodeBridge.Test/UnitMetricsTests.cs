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
        Metrics.CountToolDenial("ring2", "write", "role");
        Metrics.CountToolDenial("ring3", "shell", "token 'rm' negado pelo inspetor");
        Metrics.CountToolDenial("nudge", "edit", "faltou newString");
        var snap = Metrics.DenyCounters();
        TestContext.Check(snap["ring2_deny"]!.GetValue<long>() == 1
            && snap["ring3_deny"]!.GetValue<long>() == 1
            && snap["nudge_args"]!.GetValue<long>() == 1,
            "DenyCounters publica ring2/ring3/nudge");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Painel x API: todo portao que emite alerta tem que aparecer no painel de
    /// gates do Grafana, lado a lado com o anti-repeat. Portao novo em
    /// Metrics.AlertGateKeys sem coluna no dashboard quebra este teste.
    /// </summary>
    public static Task DashboardListsAlertGates()
    {
        string? dir = AppContext.BaseDirectory;
        string? dash = null;
        while (dir is not null && dash is null)
        {
            string probe = Path.Combine(dir, "observation", "grafana", "dashboards", "opencodebridge.json");
            if (File.Exists(probe)) dash = probe;
            dir = Path.GetDirectoryName(dir);
        }
        TestContext.Check(dash is not null, "dashboard JSON do Grafana encontrado");
        if (dash is null) return Task.CompletedTask;
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(dash))!.AsObject();
        var panel = root["panels"]!.AsArray()
            .First(x => x!["id"]!.GetValue<int>() == 5)!.AsObject();
        var cols = (panel["targets"]![0]!["columns"]!.AsArray())
            .Select(c => c!["selector"]!.GetValue<string>()).ToHashSet();
        foreach (string key in Metrics.AlertGateKeys)
            TestContext.Check(cols.Contains(key), $"painel de gates lista '{key}'");
        TestContext.Check(cols.Count == Metrics.AlertGateKeys.Length,
            $"painel tem exatamente os {Metrics.AlertGateKeys.Length} portoes (tem {cols.Count})");
        TestContext.Check(Metrics.DenyCounters().Select(kv => kv.Key).ToHashSet()
            .SetEquals(Metrics.AlertGateKeys), "DenyCounters publica exatamente os portoes de alerta");
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
