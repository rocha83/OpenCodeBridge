using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Ui;

// Simulador SSE (padrão OpenAI chat.completions) p/ E2E de UI sem GPU.
// Reusa as classes mock: os turnos roteirizados viram o corpo SSE over HTTP.
// Turno com BridgeError vira HTTP 500 (cobre evt.error/502 do site).
public sealed class SseSimulator : IDisposable
{
    private readonly HttpListener _listener;
    private readonly MockBridgeClient _mock;
    private readonly CancellationTokenSource _cts = new();
    public string BaseUrl { get; }

    public SseSimulator()
    {
        _mock = new MockBridgeClient(Options.Create(new MockBridgeOptions()));
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        Task.Run(() => Loop(_cts.Token));
    }

    public void Script(params MockBridgeClient.MockTurn[] turns)
    {
        MockBridgeClient.ClearScript();
        MockBridgeClient.EnqueueScript(turns);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "";
            if (path == "/api/status")
            {
                byte[] ok = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"mock\":true}");
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(ok);
                ctx.Response.Close();
                return;
            }
            if (ctx.Request.HttpMethod == "POST" && path.EndsWith("/chat/completions"))
            {
                if (MockBridgeClient.Script.TryPeek(out var next) && next is not null
                    && !string.IsNullOrEmpty(next.BridgeError))
                {
                    MockBridgeClient.Script.TryDequeue(out _);
                    byte[] err = Encoding.UTF8.GetBytes("{\"error\":\"" + next.BridgeError + "\"}");
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(err);
                    ctx.Response.Close();
                    return;
                }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "text/event-stream";
                var (ok, _) = await _mock.StreamAsync("", "", 0.2, "", new System.Text.Json.Nodes.JsonArray(),
                    ctx.Response.OutputStream, _cts.Token);
                _ = ok;
                try { ctx.Response.Close(); } catch { }
                return;
            }
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Close(); } catch { } }
    }
}
