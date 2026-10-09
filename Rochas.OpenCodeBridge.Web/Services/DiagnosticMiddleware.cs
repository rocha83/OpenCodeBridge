using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Rochas.OpenCodeBridge.Web.Services;

// Middleware de diagnóstico: tempo + status + (Referer e IP de origem, bem-vindos).
// Ativo só com Diagnostics:Enabled (ou env DIAGNOSTICS_ENABLED=1).
public sealed class DiagnosticMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DiagnosticOptions _options;
    private readonly ILogger<DiagnosticMiddleware> _logger;

    public DiagnosticMiddleware(RequestDelegate next, IOptions<DiagnosticOptions> options, ILogger<DiagnosticMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "request falhou {method} {path}", context.Request.Method, context.Request.Path);
            throw;
        }
        finally
        {
            sw.Stop();
            if (_options.LogRefererAndIp)
            {
                _logger.LogInformation(
                    "request {method} {path} {status} {ms}ms referer={referer} ip={ip}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    sw.ElapsedMilliseconds,
                    context.Request.Headers.Referer.ToString(),
                    context.Connection.RemoteIpAddress?.ToString());
            }
            else
            {
                _logger.LogInformation(
                    "request {method} {path} {status} {ms}ms",
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    sw.ElapsedMilliseconds);
            }
        }
    }
}
