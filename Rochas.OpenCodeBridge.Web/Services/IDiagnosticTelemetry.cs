using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rochas.Telemetry;

namespace Rochas.OpenCodeBridge.Web.Services;

// Contrato do serviço de diagnóstico (telemetria Rochas + Serilog via ILogger).
public interface IDiagnosticTelemetry
{
    bool Enabled { get; }
    Task<T> TimedAsync<T>(string action, Func<Task<T>> operation);
    T Timed<T>(string action, Func<T> operation);
    void RegisterError(Exception ex);
    object Snapshot();
}
