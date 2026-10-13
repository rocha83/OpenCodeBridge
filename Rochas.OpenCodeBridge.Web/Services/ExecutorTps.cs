namespace Rochas.OpenCodeBridge.Web.Services;

// Throughput do executor p/ ETA do painel (helper separado, sem orquestrador).
// ETA correta por engine: sondado vale; sem sonda, CPU (llama/:4125) usa
// CpuDefaultTps (3 t/s, realista) e GPU usa 20 t/s.
public static class ExecutorTps
{
    public static double ResolveTps(Models.Agent exec, double cpuDefaultTps) =>
        exec.MeasuredTps > 0 ? exec.MeasuredTps
        : IsCpuExecutor(exec) ? (cpuDefaultTps > 0 ? cpuDefaultTps : 3.0) : 20;

    public static bool IsCpuExecutor(Models.Agent exec) =>
        (exec.BridgeUrl ?? "").Contains(":4125") ||
        (exec.BridgeUrl ?? "").Contains("llama", StringComparison.OrdinalIgnoreCase) ||
        (exec.Model ?? "").Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
        (exec.Model ?? "").Contains("4b", StringComparison.OrdinalIgnoreCase);
}
