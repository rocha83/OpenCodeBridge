// Unit + integracao de Auth JWT e guarda de ctx (offline + HTTP adaptativo).
// Offline usa internals da bridge (InternalsVisibleTo); HTTP tolera bridge
// legada sem guarda (SKIP) e bridge nova com guarda (PASS com credencial).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BridgeSrv = Rochas.OpenCodeBridge;

namespace Rochas.OpenCodeBridge.Test;

internal static class UnitAuthTests
{
    public static Task JwtRoundtrip()
    {
        string old = BridgeSrv.Auth.JwtSecret; int oldExp = BridgeSrv.Auth.JwtExpiryMinutes;
        try
        {
            BridgeSrv.Auth.JwtSecret = "segredo-de-teste-com-16+";
            BridgeSrv.Auth.JwtExpiryMinutes = 60;
            string? tok = BridgeSrv.Auth.CreateToken("admin@x.y", true);
            TestContext.Check(tok is not null && tok.Split('.').Length == 3, "token em 3 partes");
            TestContext.Check(BridgeSrv.Auth.TryValidate(tok!, out string email, out bool admin), "valida");
            TestContext.Check(email == "admin@x.y" && admin, "claims preservadas");
        }
        finally { BridgeSrv.Auth.JwtSecret = old; BridgeSrv.Auth.JwtExpiryMinutes = oldExp; }
        return Task.CompletedTask;
    }

    public static Task JwtTamper()
    {
        string old = BridgeSrv.Auth.JwtSecret;
        try
        {
            BridgeSrv.Auth.JwtSecret = "segredo-de-teste-com-16+";
            string? tok = BridgeSrv.Auth.CreateToken("a@b.c", false);
            TestContext.Check(tok is not null, "token gerado");
            string[] p = tok!.Split('.');
            char last = p[2][^1];
            string bad = p[0] + "." + p[1] + "." + p[2][..^1] + (last == 'A' ? 'B' : 'A');
            TestContext.Check(!BridgeSrv.Auth.TryValidate(bad, out _, out _), "assinatura adulterada rejeitada");
            TestContext.Check(!BridgeSrv.Auth.TryValidate("invalido", out _, out _), "formato invalido rejeitado");
        }
        finally { BridgeSrv.Auth.JwtSecret = old; }
        return Task.CompletedTask;
    }

    public static Task JwtExpiry()
    {
        string old = BridgeSrv.Auth.JwtSecret; int oldExp = BridgeSrv.Auth.JwtExpiryMinutes;
        try
        {
            BridgeSrv.Auth.JwtSecret = "segredo-de-teste-com-16+";
            BridgeSrv.Auth.JwtExpiryMinutes = -60;
            string? tok = BridgeSrv.Auth.CreateToken("a@b.c", false);
            TestContext.Check(tok is not null, "token expirado gerado");
            TestContext.Check(!BridgeSrv.Auth.TryValidate(tok!, out _, out _), "expirado rejeitado");
        }
        finally { BridgeSrv.Auth.JwtSecret = old; BridgeSrv.Auth.JwtExpiryMinutes = oldExp; }
        return Task.CompletedTask;
    }

    public static Task JwtShortSecret()
    {
        string old = BridgeSrv.Auth.JwtSecret;
        try
        {
            BridgeSrv.Auth.JwtSecret = "curto";
            TestContext.Check(BridgeSrv.Auth.CreateToken("a@b.c", false) is null, "segredo curto nao assina");
            TestContext.Check(!BridgeSrv.Auth.TryValidate("x.y.z", out _, out _), "sem segredo nao valida");
        }
        finally { BridgeSrv.Auth.JwtSecret = old; }
        return Task.CompletedTask;
    }

    public static Task Pbkdf2()
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2("senha-correta", salt, 100_000, HashAlgorithmName.SHA256, 32);
        string stored = $"100000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        TestContext.Check(BridgeSrv.Auth.VerifyPassword("senha-correta", stored), "senha correta aceita");
        TestContext.Check(!BridgeSrv.Auth.VerifyPassword("senha-errada", stored), "senha errada rejeitada");
        TestContext.Check(!BridgeSrv.Auth.VerifyPassword("x", "lixo"), "formato invalido rejeitado");
        return Task.CompletedTask;
    }

    public static Task GuardTrips()
    {
        (bool g, double t, int c) = BridgeSrv.Program.ContextGuardConfig();
        try
        {
            BridgeSrv.Program.ContextGuardSetup(true, 0.95, 1000);
            TestContext.Check(BridgeSrv.Program.ContextGuardTripped("{\"usage\":{\"prompt_tokens\":950}}", out _, out _), "95% dispara");
            TestContext.Check(!BridgeSrv.Program.ContextGuardTripped("{\"usage\":{\"prompt_tokens\":949}}", out _, out _), "94.9% passa");
            BridgeSrv.Program.ContextGuardSetup(true, 0.95, 0);
            TestContext.Check(!BridgeSrv.Program.ContextGuardTripped("{\"usage\":{\"prompt_tokens\":99999}}", out _, out _), "sem contextTokens inativo");
            BridgeSrv.Program.ContextGuardSetup(false, 0.95, 1000);
            TestContext.Check(!BridgeSrv.Program.ContextGuardTripped("{\"usage\":{\"prompt_tokens\":99999}}", out _, out _), "desligado inativo");
        }
        finally { BridgeSrv.Program.ContextGuardSetup(g, t, c); }
        return Task.CompletedTask;
    }

    static async Task<(int Status, string Body)> Raw(string method, string url, JsonNode? body, string? bearer)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (bearer is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await TestContext.Http.SendAsync(req);
        return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
    }

    static async Task<bool> HasJwtRoute()
    {
        try
        {
            var spec = await TestContext.GetObj(TestContext.Bridge + "/api/swagger.json");
            return spec["paths"]?["/api/auth/login"] is not null
                && spec["components"]?["securitySchemes"]?["bearerAuth"] is not null;
        }
        catch { return false; }
    }

    public static async Task LoginDenied()
    {
        if (!await HasJwtRoute()) { Console.WriteLine("  [skip] bridge sem /api/auth/login"); return; }
        var (status, _) = await Raw("POST", TestContext.Bridge + "/api/auth/login",
            new JsonObject { ["email"] = "ninguem@invalido.zz", ["password"] = "errada" }, null);
        TestContext.Check(status == 401, $"login invalido -> 401, veio {status}");
    }

    public static async Task SwaggerJwtDoc()
    {
        if (!await HasJwtRoute()) { Console.WriteLine("  [skip] bridge sem auth JWT"); return; }
        var spec = await TestContext.GetObj(TestContext.Bridge + "/api/swagger.json");
        TestContext.Check(spec["paths"]?["/v1/chat/completions"]?["post"]?["security"] is not null, "chat protegido no doc");
    }

    public static async Task StatusGuard()
    {
        var (status, _) = await Raw("GET", TestContext.Bridge + "/api/status", null, null);
        if (status == 200) { Console.WriteLine("  [nota] bridge legada aberta (sem guarda)"); return; }
        TestContext.Check(status == 401, $"anonimo -> 401, veio {status}");
        string? cred = TestContext.ApiKey ?? TestContext.Jwt;
        if (cred is null) { Console.WriteLine("  [skip] sem --api-key/--jwt p/ retestar autenticado"); return; }
        var (st2, _) = await Raw("GET", TestContext.Bridge + "/api/status", null, cred);
        TestContext.Check(st2 == 200, $"autenticado -> 200, veio {st2}");
    }
}
