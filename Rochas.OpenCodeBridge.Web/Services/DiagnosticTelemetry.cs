using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rochas.Telemetry;

namespace Rochas.OpenCodeBridge.Web.Services;

// Diagnóstico: ComponentObserver (operações) + ServiceObserver (snapshots CPU/mem).
// Ligado via Diagnostics:Enabled (ou env DIAGNOSTICS_ENABLED=1); desligado = passthrough.
public sealed class DiagnosticTelemetry : IDiagnosticTelemetry, IDisposable
{
    private readonly DiagnosticOptions _options;
    private readonly ILogger<DiagnosticTelemetry> _logger;
    private readonly ComponentObserver _component;
    private readonly ServiceObserver _service;
    private bool _disposed;

    public DiagnosticTelemetry(IOptions<DiagnosticOptions> options, ILogger<DiagnosticTelemetry> logger)
    {
        _options = options.Value;
        string? env = Environment.GetEnvironmentVariable("DIAGNOSTICS_ENABLED");
        if (env is "1" or "true" or "True") _options.Enabled = true;
        _logger = logger;
        _component = new ComponentObserver("BridgeWeb", logger);
        _service = new ServiceObserver("BridgeWeb", logger);
        if (_options.Enabled)
        {
            _service.StartObserver(_options.SnapshotDurationMinutes, _options.SnapshotIntervalMinutes);
            _logger.LogInformation("diagnostico ligado (snapshots a cada {min}min)", _options.SnapshotIntervalMinutes);
        }
    }

    public bool Enabled => _options.Enabled;

    public async Task<T> TimedAsync<T>(string action, Func<Task<T>> operation)
    {
        if (!Enabled) return await operation();
        return await _component.ExecuteTimed(action, operation);
    }

    public T Timed<T>(string action, Func<T> operation)
    {
        if (!Enabled) return operation();
        using var activity = _component.StartActivity(action);
        try
        {
            return operation();
        }
        catch (Exception ex)
        {
            _service.RegisterError(ex);
            throw;
        }
    }

    public void RegisterError(Exception ex)
    {
        if (!Enabled) return;
        _service.RegisterError(ex);
    }

    public object Snapshot()
    {
        return new
        {
            enabled = Enabled,
            observerRunning = _service.IsObserverEnabled(),
            activities = _component.ActivityHistoryCount(),
            snapshots = _service.ExportObserverHistory().Select(s => new
            {
                timestamp = s.Timestamp,
                cpu = s.CpuPercent,
                memoryMb = s.Memory,
                threads = s.Threads,
                uptime = s.Uptime.ToString(),
                operations = s.Operations,
                errors = s.Errors,
            }).ToList(),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _service.StopObserver(); } catch { }
        try { _component.Dispose(); } catch { }
        try { _service.Dispose(); } catch { }
    }
}
