namespace Rochas.OpenCodeBridge.Web.Services;

// Opções de diagnóstico (appsettings Diagnostics + env DIAGNOSTICS_ENABLED).
public sealed class DiagnosticOptions
{
    public bool Enabled { get; set; }
    public bool LogRefererAndIp { get; set; } = true;
    public string SqliteTable { get; set; } = "logs";
    public int SnapshotIntervalMinutes { get; set; } = 5;
    public int SnapshotDurationMinutes { get; set; } = 60;
}
