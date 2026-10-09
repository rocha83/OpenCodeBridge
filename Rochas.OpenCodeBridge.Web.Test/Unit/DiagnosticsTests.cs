// Testes unitários de diagnóstico (telemetria, middleware, senhas) — sem rede.
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130

using System.IO;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class DiagnosticsTests
{
    private static int Failures;

    internal static int Run()
    {
        DiagDisabledPassthrough();
        DiagEnabledTimed();
        DiagSnapshotShape();
        DiagMiddlewareLogsRefererIp().GetAwaiter().GetResult();
        DiagMiddlewareSilentWhenOff().GetAwaiter().GetResult();
        PassRoundtrip();

        System.Console.WriteLine($"=== Diagnostics Unit: {6 - Failures}/6 PASS, {Failures} FAIL ===");
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public readonly System.Collections.Generic.List<string> Lines = new();
        IDisposable ILogger.BeginScope<TState>(TState state) => null!;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Lines.Add(formatter(state, ex));
    }

    private static DiagnosticTelemetry Telemetry(bool enabled)
    {
        var options = Options.Create(new DiagnosticOptions { Enabled = enabled });
        return new DiagnosticTelemetry(options, NullLogger<DiagnosticTelemetry>.Instance);
    }

    private static void DiagDisabledPassthrough()
    {
        using var t = Telemetry(false);
        int v = t.Timed("x", () => 42);
        Check(!t.Enabled && v == 42, "U-diag-disabled-passthrough");
    }

    private static void DiagEnabledTimed()
    {
        using var t = Telemetry(true);
        string v = t.Timed("greet", () => "ola");
        var snap = JsonSerializer.Serialize(t.Snapshot());
        Check(t.Enabled && v == "ola" && snap.Contains("\"enabled\":true"), "U-diag-enabled-timed");
    }

    private static void DiagSnapshotShape()
    {
        using var t = Telemetry(true);
        var snap = JsonSerializer.Serialize(t.Snapshot());
        Check(snap.Contains("observerRunning") && snap.Contains("snapshots"), "U-diag-snapshot-shape");
    }

    private static async System.Threading.Tasks.Task DiagMiddlewareLogsRefererIp()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/Chat";
        ctx.Request.Headers.Referer = "http://origem.local/x";
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.9");
        ctx.Response.Body = new MemoryStream();
        var logger = new ListLogger<DiagnosticMiddleware>();
        var mw = new DiagnosticMiddleware(
            _ => { ctx.Response.StatusCode = 200; return System.Threading.Tasks.Task.CompletedTask; },
            Options.Create(new DiagnosticOptions { Enabled = true }),
            logger);
        await mw.InvokeAsync(ctx);
        Check(logger.Lines.Exists(l => l.Contains("http://origem.local/x") && l.Contains("10.0.0.9") && l.Contains("200")),
            "U-diag-middleware-log");
    }

    private static async System.Threading.Tasks.Task DiagMiddlewareSilentWhenOff()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/Chat";
        ctx.Response.Body = new MemoryStream();
        var logger = new ListLogger<DiagnosticMiddleware>();
        var mw = new DiagnosticMiddleware(
            _ => { ctx.Response.StatusCode = 200; return System.Threading.Tasks.Task.CompletedTask; },
            Options.Create(new DiagnosticOptions { Enabled = false }),
            logger);
        await mw.InvokeAsync(ctx);
        Check(logger.Lines.Count == 0 && ctx.Response.StatusCode == 200, "U-diag-middleware-silent");
    }

    private static void PassRoundtrip()
    {
        IPasswordHasher p = new PasswordHasherService();
        string h = p.Hash("Segredo@123");
        Check(p.Verify("Segredo@123", h) && !p.Verify("errada", h), "U-pass-roundtrip");
    }
}
