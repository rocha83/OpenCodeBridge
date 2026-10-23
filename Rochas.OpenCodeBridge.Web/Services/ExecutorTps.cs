namespace Rochas.OpenCodeBridge.Web.Services;

// Throughput do executor p/ ETA do painel (helper separado, sem orquestrador).
// Medido em 14/10: CPU 4B 2.5 t/s, GPU 9B 30 t/s. Sondado vale; sem sonda,
// CPU (llama/:4125) usa CpuDefaultTps e GPU usa 30 t/s.
public static class ExecutorTps
{
    public static double ResolveTps(Models.Agent exec, double cpuDefaultTps) =>
        exec.MeasuredTps > 0 ? exec.MeasuredTps
        : IsCpuExecutor(exec) ? (cpuDefaultTps > 0 ? cpuDefaultTps : 2.5) : 30;

    public static bool IsCpuExecutor(Models.Agent exec) =>
        (exec.BridgeUrl ?? "").Contains(":4125") ||
        (exec.BridgeUrl ?? "").Contains(":4110") ||
        (exec.BridgeUrl ?? "").Contains("llama", StringComparison.OrdinalIgnoreCase) ||
        (exec.Model ?? "").Contains("cpu", StringComparison.OrdinalIgnoreCase) ||
        (exec.Model ?? "").Contains("4b", StringComparison.OrdinalIgnoreCase);
}
