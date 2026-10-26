using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>Opcoes do preflight do stack local usadas pelas suites de teste.</summary>
public sealed record StackPreflightOptions
{
    /// <summary>Bridge da GPU (gate). Default <c>http://127.0.0.1:4124</c>.</summary>
    public string BridgeUrl { get; init; } = "http://127.0.0.1:4124";

    /// <summary>Llama da GPU (upstream do gate). Default <c>http://127.0.0.1:4100</c>.</summary>
    public string LlamaUrl { get; init; } = "http://127.0.0.1:4100";

    /// <summary>Bridge espelho/CPU (Q3). Default <c>http://127.0.0.1:4125</c>.</summary>
    public string? Q3BridgeUrl { get; init; } = "http://127.0.0.1:4125";

    /// <summary>Llama do espelho (Q3). Default <c>http://127.0.0.1:4110</c>.</summary>
    public string? Q3LlamaUrl { get; init; } = "http://127.0.0.1:4110";

    /// <summary>URL da Web UI (quando a suite testa a Web).</summary>
    public string? WebUrl { get; init; }

    /// <summary>ApiKey/JWT das bridges.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Exige o espelho Q3 (bridge + modelo respondendo).</summary>
    public bool RequireQ3 { get; init; } = true;

    /// <summary>Exige a Web (login + ping autenticado).</summary>
    public bool RequireWeb { get; init; }

    /// <summary>Faz uma completion minima em cada modelo exigido.</summary>
    public bool ProbeModel { get; init; } = true;

    /// <summary>Falha se outro runner estiver ativo. O driver marca os filhos com BRIDGE_UNDER_DRIVER=1.</summary>
    public bool CheckLock { get; init; } = true;

    /// <summary>Arquivo de lock do driver.</summary>
    public string LockPath { get; init; } = Path.Combine(Path.GetTempPath(), "opencode", "circuit.lock");

    /// <summary>VRAM livre minima (MiB); ignorada quando nvidia-smi nao existe.</summary>
    public int MinFreeVramMiB { get; init; } = 512;

    /// <summary>Email do login de teste da Web.</summary>
    public string WebEmail { get; init; } = "admin@bridge.pcss.com.br";

    /// <summary>Senha do login de teste da Web.</summary>
    public string WebPassword { get; init; } = "123";
}

/// <summary>Um check do preflight.</summary>
/// <param name="Name">Nome curto do item.</param>
/// <param name="Ok">Resultado.</param>
/// <param name="Detail">Detalhe/evidencia.</param>
public sealed record StackPreflightCheck(string Name, bool Ok, string Detail);

/// <summary>Resultado do preflight (todos os checks + veredito).</summary>
public sealed class StackPreflightResult
{
    private readonly List<StackPreflightCheck> _checks = new();

    /// <summary>Checks executados, na ordem.</summary>
    public IReadOnlyList<StackPreflightCheck> Checks => _checks;

    /// <summary>GO somente quando todos os checks passam.</summary>
    public bool Ok => _checks.All(c => c.Ok);

    internal void Add(string name, bool ok, string detail)
        => _checks.Add(new StackPreflightCheck(name, ok, detail));

    /// <summary>Imprime o relatorio no formato da suite e devolve o veredito.</summary>
    public bool Print(Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        foreach (var check in _checks)
            log($"[preflight] {(check.Ok ? "ok  " : "FAIL")} {check.Name} — {check.Detail}");
        log(Ok ? "[preflight] GO: stack pronto" : "[preflight] NO-GO: corrija os FAIL acima");
        return Ok;
    }
}

/// <summary>
/// Preflight do stack local (llama Q4/Q3, bridges, Web e VRAM) executado ANTES
/// de qualquer teste que fale com o modelo. Mesma regra para a suite da bridge
/// e a da Web: falha rapido com a lista do que esta fora, em vez de gastar
/// 30 min de belt para descobrir que uma porta estava fechada.
/// </summary>
public static class StackPreflight
{
    /// <summary>Executa os checks e devolve o resultado (nao lanca por stack fora).</summary>
    public static StackPreflightResult Run(StackPreflightOptions options, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new StackPreflightResult();
        using var http = NewClient(TimeSpan.FromSeconds(20));

        if (options.CheckLock)
        {
            var (ok, detail) = CheckLock(options.LockPath);
            result.Add("lock runner", ok, detail);
        }

        var q4 = CheckBridge(http, options.BridgeUrl, options.ApiKey, out string q4Detail);
        result.Add($"bridge-gpu {options.BridgeUrl}", q4 is not null, q4Detail);
        if (q4 is not null)
        {
            var (ok, detail) = CheckLlama(http, options.BridgeUrl, options.ApiKey);
            result.Add("llama-gpu", ok, detail);
        }

        if (options.RequireQ3 && !string.IsNullOrWhiteSpace(options.Q3BridgeUrl))
        {
            var q3 = CheckBridge(http, options.Q3BridgeUrl!, options.ApiKey, out string q3Detail);
            result.Add($"bridge-gpu2 {options.Q3BridgeUrl}", q3 is not null, q3Detail);
            if (q3 is not null)
            {
                string upstream = q3["upstream"]?.GetValue<string>() ?? "";
                bool points = !string.IsNullOrWhiteSpace(options.Q3LlamaUrl)
                    && string.Equals(upstream.TrimEnd('/'),
                        options.Q3LlamaUrl!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
                result.Add("bridge-gpu2 upstream", points,
                    $"upstream={upstream}{(points ? "" : $" (esperado {options.Q3LlamaUrl})")}");
                var (ok, detail) = CheckLlama(http, options.Q3BridgeUrl!, options.ApiKey);
                result.Add("llama-gpu2", ok, detail);
            }
        }

        if (options.ProbeModel)
        {
            if (q4 is not null)
            {
                var (ok, detail) = Probe(options.BridgeUrl, options.ApiKey, ModelOf(q4));
                result.Add("modelo bridge responde", ok, detail);
            }
            if (options.RequireQ3 && !string.IsNullOrWhiteSpace(options.Q3BridgeUrl))
            {
                var q3 = TryGetJson(http, options.Q3BridgeUrl!.TrimEnd('/') + "/api/status", options.ApiKey);
                if (q3 is not null)
                {
                    var (ok, detail) = Probe(options.Q3BridgeUrl!, options.ApiKey, ModelOf(q3));
                    result.Add("modelo Q3 responde", ok, detail);
                }
            }
        }

        if (options.RequireWeb && !string.IsNullOrWhiteSpace(options.WebUrl))
        {
            var (ok, detail) = CheckWeb(options.WebUrl!, options.WebEmail, options.WebPassword);
            result.Add($"web {options.WebUrl}", ok, detail);
        }

        var (vramOk, vramDetail) = CheckVram(options.MinFreeVramMiB);
        result.Add("vram livre", vramOk, vramDetail);

        return result;
    }

    private static HttpClient NewClient(TimeSpan timeout)
        => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout };

    private static HttpClient NewClientWithCookies(TimeSpan timeout, CookieContainer cookies)
        => new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies })
        {
            Timeout = timeout
        };

    private static JsonNode? CheckBridge(HttpClient http, string url, string? apiKey, out string detail)
    {
        var node = TryGetJson(http, url.TrimEnd('/') + "/api/status", apiKey);
        if (node is null)
        {
            detail = "sem resposta em /api/status";
            return null;
        }
        string status = node["status"]?.GetValue<string>() ?? "";
        detail = $"status={status} model={node["model"]}";
        return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase) ? node : null;
    }

    private static (bool Ok, string Detail) CheckLlama(HttpClient http, string bridgeUrl, string? apiKey)
    {
        var node = TryGetJson(http, bridgeUrl.TrimEnd('/') + "/api/status", apiKey);
        string upstream = node?["upstream"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(upstream))
            return (false, "bridge sem upstream");
        var (code, _) = Get(http, upstream.TrimEnd('/') + "/health", apiKey);
        return code == 200 ? (true, $"health 200 ({upstream})") : (false, $"health HTTP {code} ({upstream})");
    }

    private static (bool Ok, string Detail) Probe(
        string bridgeUrl, string? apiKey, string model)
    {
        // Liveness do modelo pode esperar fila: se o slot estiver ocupado (run
        // anterior morto no meio de uma geracao), 20s davam NO-GO falso.
        using var http = NewClient(TimeSpan.FromSeconds(180));
        var payload = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 8,
            ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "ok"
            })
        };
        var (code, body) = PostJson(http, bridgeUrl.TrimEnd('/') + "/v1/chat/completions", apiKey, payload);
        if (code != 200)
            return (false, $"HTTP {code}: {Short(body)}");
        try
        {
            var node = JsonNode.Parse(body);
            string finish = node?["choices"]?[0]?["finish_reason"]?.GetValue<string>() ?? "";
            return node?["choices"] is JsonArray ? (true, $"finish={finish}") : (false, "resposta sem choices");
        }
        catch (Exception ex)
        {
            return (false, $"json invalido: {ex.Message}");
        }
    }

    private static (bool Ok, string Detail) CheckWeb(
        string web, string email, string password)
    {
        var cookies = new CookieContainer();
        using var http = NewClientWithCookies(TimeSpan.FromSeconds(20), cookies);
        var (code, html) = Get(http, web.TrimEnd('/') + "/Account/Login");
        if (code != 200) return (false, $"login HTTP {code}");

        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        if (!match.Success) return (false, "antiforgery ausente no login");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["__RequestVerificationToken"] = match.Groups[1].Value
        });
        var (loginCode, _) = Post(http, web.TrimEnd('/') + "/Account/Login", form);
        if (loginCode is not (200 or 302))
            return (false, $"login POST HTTP {loginCode}");

        var (pingCode, ping) = Get(http, web.TrimEnd('/') + "/Chat/Ping?agentId=1");
        bool ok = pingCode == 200 && ping.Contains("\"ok\":true", StringComparison.Ordinal);
        return (ok, $"login={loginCode} ping={pingCode}{(ok ? "" : " (sem ok:true)")}");
    }

    private static (bool Ok, string Detail) CheckLock(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var stream = new FileStream(
                path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return (true, "livre");
        }
        catch (IOException)
        {
            return (false, $"outro runner ativo ({path})");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, $"sem permissao no lock ({path})");
        }
    }

    private static (bool Ok, string Detail) CheckVram(int minMiB)
    {
        try
        {
            var info = new ProcessStartInfo("nvidia-smi")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            info.ArgumentList.Add("--query-gpu=memory.free");
            info.ArgumentList.Add("--format=csv,noheader,nounits");
            using var process = Process.Start(info);
            if (process is null) return (true, "n/d (sem nvidia-smi)");
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            string first = output.Split('\n')[0].Trim();
            if (!int.TryParse(first, out int free)) return (true, "n/d");
            return (free >= minMiB, $"{free} MiB livres (min {minMiB})");
        }
        catch
        {
            return (true, "n/d (sem nvidia-smi)");
        }
    }

    private static string ModelOf(JsonNode bridge)
        => bridge["model"]?.GetValue<string>() ?? "";

    private static (int Code, string Body) Get(HttpClient http, string url, string? apiKey = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new("Bearer", apiKey);
            using var response = http.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return ((int)response.StatusCode, reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    private static (int Code, string Body) Post(
        HttpClient http, string url, HttpContent content, string? apiKey = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new("Bearer", apiKey);
            using var response = http.Send(request);
            using var reader = new StreamReader(response.Content.ReadAsStream());
            return ((int)response.StatusCode, reader.ReadToEnd());
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    private static (int Code, string Body) PostJson(
        HttpClient http, string url, string? apiKey, JsonNode payload)
        => Post(http, url,
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            apiKey);

    private static JsonNode? TryGetJson(HttpClient http, string url, string? apiKey)
    {
        var (code, body) = Get(http, url, apiKey);
        if (code != 200) return null;
        try { return JsonNode.Parse(body); }
        catch { return null; }
    }

    private static string Short(string text)
        => text.Length <= 160 ? text : text[..160].Replace('\n', ' ');
}
