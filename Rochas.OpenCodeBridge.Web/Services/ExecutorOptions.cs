namespace Rochas.OpenCodeBridge.Web.Services;

// Default de throughput do executor usado no ETA do painel. O valor medido no
// agente (measured_tps) sempre vence; este default só cobre a ausência de sonda.
public sealed class ExecutorOptions
{
    public double CpuDefaultTps { get; set; } = 2.5;
}
