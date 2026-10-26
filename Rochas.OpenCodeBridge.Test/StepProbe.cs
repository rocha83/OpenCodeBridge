using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
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
    // 5s: o tick faz nvidia-smi + /api/status + /api/metrics; a 2s o proprio
    // monitoramento pesava no t/s do step. Throughput e VRAM usam a mesma sonda.
    private const int ProbeIntervalMs = 5000;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly ILogger _logger;
    private readonly string _step;
    private readonly string _mode;
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
        _mode = mode;
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
                try { await Task.Delay(ProbeIntervalMs, _cts.Token); }
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

        string statusJson = "";
        string metricsJson = "";
        if (!string.IsNullOrWhiteSpace(_probeUrl))
        {
            statusJson = await TryGetAsync(_probeUrl + "/api/status", _apiKey);
            metricsJson = await TryGetAsync(_probeUrl + "/api/metrics", _apiKey);
            props["status"] = statusJson;
            props["metrics"] = metricsJson;
        }

        props["processes"] = ProcessSnapshot();
        long diskFree = DiskFreeMb(_probeDir);
        props["disk_free_mb"] = diskFree;

        _logger.Information("probe tick {@Props}", props);
        await PublishSampleAsync(elapsed, gpu, statusJson, metricsJson, diskFree);

        foreach (string logPath in _logPaths)
            TailLog(logPath);
    }

    private async Task PublishSampleAsync(double elapsed, string gpu, string statusJson, string metricsJson, long diskFree)
    {
        if (string.IsNullOrWhiteSpace(_probeUrl)) return;
        var (util, usedMb, totalMb, temp) = ParseGpu(gpu);
        var sample = new JsonObject
        {
            ["step"] = _step,
            ["mode"] = _mode,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["time"] = DateTime.UtcNow.ToString("o"),
            ["elapsed_s"] = Math.Round(elapsed, 1),
            ["tps_output"] = Num(metricsJson, "tps_output"),
            ["tps_total"] = Num(metricsJson, "tps_total"),
            ["tools_calls"] = Num(metricsJson, "tools_calls"),
            ["requests"] = Num(metricsJson, "requests"),
            ["requests_opencode"] = Num(metricsJson, "requests_opencode"),
            ["requests_bridge"] = Num(metricsJson, "requests_bridge"),
            ["vram_used_mb"] = usedMb,
            ["vram_total_mb"] = totalMb,
            ["gpu_util"] = util,
            ["gpu_temp"] = temp,
            ["disk_free_mb"] = diskFree,
            ["temperature"] = Num(statusJson, "temperature"),
            ["context_tokens"] = Num(statusJson, "context_tokens")
        };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _probeUrl + "/api/test/probe");
            if (!string.IsNullOrWhiteSpace(_apiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(sample.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                _logger.Debug("probe publish http={Status} step={Step}", (int)res.StatusCode, _step);
        }
        catch (Exception ex) { _logger.Debug("probe publish erro: {Error}", ex.Message); }
    }

    private static double Num(string json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try { return JsonNode.Parse(json)?[name]?.GetValue<double>() ?? 0; }
        catch { return 0; }
    }

    private static (double Util, double UsedMb, double TotalMb, double Temp) ParseGpu(string gpu)
    {
        if (string.IsNullOrWhiteSpace(gpu) || gpu is "n/a" or "erro") return (-1, -1, -1, -1);
        string[] p = gpu.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return (-1, -1, -1, -1);
        static double D(string s) => double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : -1;
        return (D(p[0]), D(p[1]), D(p[2]), p.Length > 3 ? D(p[3]) : -1);
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
