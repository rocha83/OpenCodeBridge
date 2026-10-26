// ===========================================================================
//  Testes do preflight do stack (StackPreflight): prova GO e cada NO-GO com
//  servidor HTTP falso em 127.0.0.1. Sem modelo, sem bridge real, sem rede
//  externa — se o check passar aqui, passa no stack; se falhar, aponta o item.
// ===========================================================================

using System.Net;
using System.Net.Sockets;
using System.Text;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Test;

internal static class UnitStackPreflightTests
{
    /// <summary>Servidor HTTP minimo (HttpListener) para simular bridge/llama/web.</summary>
    private sealed class FakeHttp : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<HttpListenerContext, (int Status, string Body)> _handler;
        private readonly Task _loop;

        public string BaseUrl { get; }

        public FakeHttp(Func<HttpListenerContext, (int Status, string Body)> handler)
        {
            _handler = handler;
            BaseUrl = $"http://127.0.0.1:{FreePort()}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var (status, body) = _handler(ctx);
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                        ctx.Response.Close();
                    }
                    catch
                    {
                        try { ctx.Response.Abort(); } catch { }
                    }
                });
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
            try { _loop.Wait(500); } catch { }
        }
    }

    private static StackPreflightOptions Options(
        string bridge, string? q3 = null, string? web = null, bool requireQ3 = false,
        bool probe = true, bool checkLock = false, string? lockPath = null)
        => new()
        {
            BridgeUrl = bridge,
            LlamaUrl = bridge,
            Q3BridgeUrl = q3,
            Q3LlamaUrl = "http://127.0.0.1:4110",
            WebUrl = web,
            ApiKey = "teste",
            RequireQ3 = requireQ3,
            RequireWeb = web is not null,
            ProbeModel = probe,
            CheckLock = checkLock,
            LockPath = lockPath ?? Path.Combine(Path.GetTempPath(), "opencode",
                "preflight-test-" + Guid.NewGuid().ToString("N") + ".lock"),
            MinFreeVramMiB = 0
        };

    /// <summary>Bridge + llama + modelo respondendo = GO em todos os checks.</summary>
    public static Task Go()
    {
        using var llama = new FakeHttp(ctx =>
            ctx.Request.Url!.AbsolutePath == "/health"
                ? (200, "{\"ok\":true}")
                : (404, "{}"));
        using var bridge = new FakeHttp(ctx => ctx.Request.Url!.AbsolutePath switch
        {
            "/api/status" => (200,
                $"{{\"status\":\"ok\",\"model\":\"fake-model\",\"upstream\":\"{llama.BaseUrl}\"}}"),
            "/v1/chat/completions" => (200, "{\"choices\":[{\"finish_reason\":\"stop\"}]}"),
            _ => (404, "{}")
        });

        var result = StackPreflight.Run(Options(bridge.BaseUrl));
        TestContext.Check(result.Ok, $"deveria ser GO: {Dump(result)}");
        TestContext.Check(result.Checks.Any(c => c.Name.StartsWith("bridge-gpu", StringComparison.Ordinal) && c.Ok),
            "bridge-gpu deveria passar");
        TestContext.Check(result.Checks.Any(c => c.Name == "llama-gpu" && c.Ok),
            "llama-gpu deveria passar");
        TestContext.Check(result.Checks.Any(c => c.Name == "modelo bridge responde" && c.Ok),
            "modelo bridge deveria responder");
        return Task.CompletedTask;
    }

    /// <summary>Porta fechada = NO-GO imediato com o nome do item.</summary>
    public static Task BridgeDown()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var result = StackPreflight.Run(Options($"http://127.0.0.1:{port}", probe: false));
        TestContext.Check(!result.Ok, "porta fechada deveria ser NO-GO");
        TestContext.Check(result.Checks.Any(c =>
                c.Name.StartsWith("bridge-gpu", StringComparison.Ordinal) && !c.Ok),
            $"deveria acusar bridge-gpu: {Dump(result)}");
        return Task.CompletedTask;
    }

    /// <summary>Modelo devolvendo 500 = NO-GO so no check do modelo.</summary>
    public static Task ModelFails()
    {
        using var llama = new FakeHttp(_ => (200, "{\"ok\":true}"));
        using var bridge = new FakeHttp(ctx => ctx.Request.Url!.AbsolutePath switch
        {
            "/api/status" => (200,
                $"{{\"status\":\"ok\",\"model\":\"fake-model\",\"upstream\":\"{llama.BaseUrl}\"}}"),
            "/v1/chat/completions" => (500, "{\"error\":\"modelo caido\"}"),
            _ => (404, "{}")
        });

        var result = StackPreflight.Run(Options(bridge.BaseUrl));
        TestContext.Check(!result.Ok, "modelo 500 deveria ser NO-GO");
        TestContext.Check(result.Checks.Any(c => c.Name == "modelo bridge responde" && !c.Ok),
            $"deveria acusar modelo: {Dump(result)}");
        TestContext.Check(result.Checks.Where(c => c.Name != "modelo bridge responde").All(c => c.Ok),
            $"apenas o modelo deveria falhar: {Dump(result)}");
        return Task.CompletedTask;
    }

    /// <summary>Q3 apontando para upstream errado = NO-GO no check de upstream.</summary>
    public static Task Q3UpstreamMismatch()
    {
        using var q3 = new FakeHttp(ctx => ctx.Request.Url!.AbsolutePath == "/api/status"
            ? (200, "{\"status\":\"ok\",\"model\":\"q3-fake\",\"upstream\":\"http://127.0.0.1:9999\"}")
            : (404, "{}"));

        var result = StackPreflight.Run(
            Options("http://127.0.0.1:9999", q3.BaseUrl, requireQ3: true, probe: false));
        TestContext.Check(!result.Ok, "upstream divergente deveria ser NO-GO");
        TestContext.Check(result.Checks.Any(c => c.Name == "bridge-gpu2 upstream" && !c.Ok),
            $"deveria acusar upstream: {Dump(result)}");
        return Task.CompletedTask;
    }

    /// <summary>Lock ocupado (outro runner) = NO-GO com o caminho.</summary>
    public static Task LockHeld()
    {
        string path = Path.Combine(Path.GetTempPath(), "opencode",
            "preflight-lock-" + Guid.NewGuid().ToString("N") + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var held = new FileStream(path, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            var result = StackPreflight.Run(
                Options("http://127.0.0.1:9999", probe: false, checkLock: true, lockPath: path));
            TestContext.Check(!result.Ok, "lock ocupado deveria ser NO-GO");
            TestContext.Check(result.Checks.Any(c => c.Name == "lock runner" && !c.Ok),
                $"deveria acusar lock: {Dump(result)}");
        }
        finally
        {
            held.Dispose();
            try { File.Delete(path); } catch { }
        }
        return Task.CompletedTask;
    }

    /// <summary>Login+ping da Web: com antiforgery/cookie = ok; sem token = NO-GO.</summary>
    public static Task WebLogin()
    {
        const string page = "<form method=\"post\">"
            + "<input name=\"__RequestVerificationToken\" value=\"tok123\"></form>";
        using var web = new FakeHttp(ctx =>
        {
            string path = ctx.Request.Url!.AbsolutePath;
            if (path == "/Account/Login" && ctx.Request.HttpMethod == "GET")
                return (200, page);
            if (path == "/Account/Login" && ctx.Request.HttpMethod == "POST")
            {
                ctx.Response.Headers.Add("Set-Cookie", "bridge_auth=1; Path=/");
                return (302, "");
            }
            if (path == "/Chat/Ping")
            {
                string cookie = ctx.Request.Headers["Cookie"] ?? "";
                return cookie.Contains("bridge_auth=1", StringComparison.Ordinal)
                    ? (200, "{\"ok\":true}")
                    : (401, "{\"ok\":false}");
            }
            return (404, "{}");
        });

        var ok = StackPreflight.Run(Options("http://127.0.0.1:9999", web: web.BaseUrl, probe: false));
        TestContext.Check(ok.Checks.Any(c => c.Name.StartsWith("web ", StringComparison.Ordinal) && c.Ok),
            $"login+ping deveria passar: {Dump(ok)}");

        using var noToken = new FakeHttp(_ => (200, "<form>sem token</form>"));
        var bad = StackPreflight.Run(Options("http://127.0.0.1:9999", web: noToken.BaseUrl, probe: false));
        TestContext.Check(bad.Checks.Any(c => c.Name.StartsWith("web ", StringComparison.Ordinal) && !c.Ok),
            $"pagina sem antiforgery deveria falhar: {Dump(bad)}");
        return Task.CompletedTask;
    }

    private static string Dump(StackPreflightResult result)
        => string.Join(" | ", result.Checks.Select(c => $"{(c.Ok ? "ok" : "FAIL")} {c.Name}: {c.Detail}"));
}
