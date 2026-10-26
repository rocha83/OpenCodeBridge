using System.Diagnostics;
using System.Text;
using Serilog;
using Serilog.Events;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Sonda estruturada por step (padrao Serilog): persiste em arquivo JSON
/// rolante e emite no console, com propriedades estruturadas para
/// acompanhamento em tempo real (Grafana/Loki ou tail do arquivo).
/// </summary>
internal sealed class StepProbe : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ILogger _logger;
    private readonly string _step;
    private readonly string _probeDir;
    private readonly string _probeUrl;
    private readonly string _apiKey;
    private readonly IReadOnlyList<string> _logPaths;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly Dictionary<string, long> _offsets = new();

    public static StepProbe Start(
        string runDir, string step, string mode,
        string? probeUrl, string? apiKey, IEnumerable<string>? logPaths)
    {
        string probeDir = Path.Combine(runDir, "probes");
        Directory.CreateDirectory(probeDir);
        string safe = Sanitize(step);

        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("Step", step)
            .Enrich.WithProperty("Mode", mode)
            .Enrich.WithProperty("ProbeDir", probeDir)
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] probe {Step} {Message:lj} {Properties:j}{NewLine}")
            .WriteTo.File(
                Path.Combine(probeDir, safe + ".probe.json"),
                rollingInterval: RollingInterval.Day,
                shared: true,
                outputTemplate: "{@t:O} [{@l:u3}] {@m} {Properties:j}{NewLine}")
            .CreateLogger();

        return new StepProbe(probeDir, step, mode, probeUrl ?? "", apiKey ?? "", logPaths ?? Array.Empty<string>(), logger);
    }

    private StepProbe(
        string probeDir, string step, string mode, string probeUrl, string apiKey,
        IEnumerable<string> logPaths, ILogger logger)
    {
        _probeDir = probeDir;
        _step = step;
        _probeUrl = probeUrl;
        _apiKey = apiKey;
        _logPaths = logPaths.ToArray();
        _logger = logger;

        _logger.Information("probe start step={Step} mode={Mode} url={ProbeUrl}", step, mode, probeUrl);

        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await TickAsync(); }
                catch (Exception ex) { _logger.Warning("probe tick erro: {Error}", ex.Message); }
                try { await Task.Delay(2000, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    private async Task TickAsync()
    {
        double elapsed = (DateTime.UtcNow - _started).TotalSeconds;

        string gpu = TryNvidia();
        var props = new Dictionary<string, object>
        {
            ["elapsed_s"] = Math.Round(elapsed, 1),
            ["gpu"] = gpu
        };

        if (!string.IsNullOrWhiteSpace(_probeUrl))
        {
            props["status"] = await TryGetAsync(_probeUrl + "/api/status", _apiKey);
            props["metrics"] = await TryGetAsync(_probeUrl + "/api/metrics", _apiKey);
        }

        props["processes"] = ProcessSnapshot();
        props["disk_free_mb"] = DiskFreeMb(_probeDir);

        _logger.Information("probe tick {@Props}", props);

        foreach (string logPath in _logPaths)
            TailLog(logPath);
    }

    private void TailLog(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long len = fs.Length;
            if (!_offsets.TryGetValue(path, out long off))
            {
                off = len;
                _offsets[path] = off;
                return;
            }
            if (len < off) off = 0;
            if (len <= off) return;

            fs.Seek(off, SeekOrigin.Begin);
            byte[] buf = new byte[len - off];
            int read = fs.Read(buf, 0, buf.Length);
            _offsets[path] = off + read;
            string text = Encoding.UTF8.GetString(buf, 0, read).Trim();
            if (text.Length > 0)
                _logger.Information("logtail path={Path} bytes={Bytes} text={Text}", path, read, text);
        }
        catch (Exception ex)
        {
            _logger.Warning("logtail erro {Path}: {Error}", path, ex.Message);
        }
    }

    private static string TryNvidia()
    {
        string? nvidia = Execution.FindOnPath("nvidia-smi");
        if (nvidia is null) return "n/a";
        try
        {
            var run = Execution.Run(
                new CircuitCommand(nvidia,
                    new[] { "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu", "--format=csv,noheader,nounits" },
                    null, null), 3);
            return run.Code == 0 ? run.Output.Trim().Replace("\n", " | ") : "erro";
        }
        catch { return "erro"; }
    }

    private async Task<string> TryGetAsync(string url, string apiKey)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var res = await _http.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();
            return res.IsSuccessStatusCode ? body.Trim() : $"http={ (int)res.StatusCode}";
        }
        catch (Exception ex) { return "erro=" + ex.Message; }
    }

    private static string ProcessSnapshot()
    {
        var names = new[] { "dotnet", "opencode", "grafana", "llama" };
        var parts = new List<string>();
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (names.Any(n => p.ProcessName.Contains(n, StringComparison.OrdinalIgnoreCase)))
                        parts.Add($"{p.ProcessName}#{p.Id}");
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return string.Join(",", parts.Take(40));
    }

    private static long DiskFreeMb(string dir)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(dir)) ?? "/";
            return new DriveInfo(root).AvailableFreeSpace / 1024 / 1024;
        }
        catch { return -1; }
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(3000); } catch { }
        _logger.Information("probe end step={Step} elapsed_s={Elapsed:F1}", _step, (DateTime.UtcNow - _started).TotalSeconds);
        (_logger as IDisposable)?.Dispose();
        _cts.Dispose();
    }
}
