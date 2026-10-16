// Replique do JWT no Web UI: o browser fala com a Web (cookie) e a Web
// fala com a bridge (Bearer JWT). Login contra /api/auth/login da bridge
// com a conta de servico (mesmos usuarios do admin), token em cache por
// bridge. Comentarios pt-BR, identificadores en-US.
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

public sealed class BridgeAuthOptions
{
    public string Email { get; set; } = "admin@bridge.pcss.com.br";
    public string Password { get; set; } = "123";
}

public sealed class BridgeAuthService(IHttpClientFactory http, Microsoft.Extensions.Options.IOptions<BridgeAuthOptions> opt, ILogger<BridgeAuthService> log)
{
    private readonly Dictionary<string, (string Token, DateTimeOffset Exp)> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetTokenAsync(string bridgeUrl, CancellationToken ct, bool force = false)
    {
        string key = bridgeUrl.TrimEnd('/').ToLowerInvariant();
        if (!force && _cache.TryGetValue(key, out var hit) && hit.Exp > DateTimeOffset.UtcNow.AddMinutes(5))
            return hit.Token;
        await _gate.WaitAsync(ct);
        try
        {
            if (!force && _cache.TryGetValue(key, out hit) && hit.Exp > DateTimeOffset.UtcNow.AddMinutes(5))
                return hit.Token;
            var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var body = new JsonObject { ["email"] = opt.Value.Email, ["password"] = opt.Value.Password };
            using var res = await client.PostAsync(key + "/api/auth/login",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            if (!res.IsSuccessStatusCode) { log.LogWarning("BridgeAuth login {Bridge} -> {Status}", key, (int)res.StatusCode); return null; }
            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?.AsObject();
            string? token = json?["token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token)) return null;
            int ttl = json?["expires_in"]?.GetValue<int>() ?? 3600;
            _cache[key] = (token, DateTimeOffset.UtcNow.AddSeconds(ttl - 60));
            return token;
        }
        catch (Exception ex) { log.LogWarning(ex, "BridgeAuth login {Bridge} falhou", key); return null; }
        finally { _gate.Release(); }
    }
}

// Handler do HttpClient nomeado "bridge": injeta Bearer JWT (com 1 retry em 401).
public sealed class BridgeAuthHandler(BridgeAuthService auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string baseUrl = (request.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "").ToLowerInvariant();
        string? token = await auth.GetTokenAsync(baseUrl, ct);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await base.SendAsync(request, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            token = await auth.GetTokenAsync(baseUrl, ct, force: true);
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                res.Dispose();
                res = await base.SendAsync(request, ct);
            }
        }
        return res;
    }
}
