// ===========================================================================
//  Auth da Qwen3-Bridge — JWT HS256 + login contra a base de usuarios do Web.
// ---------------------------------------------------------------------------
//  Sem pacotes NuGet (BCL: HMACSHA256 + PBKDF2 + Microsoft.Data.Sqlite).
//  Login valida email/senha na tabela users do web.db (mesmo hash PBKDF2
//  do Web); o token carrega email + is_admin. Endpoints /v1/* e /api de
//  telemetria aceitam Bearer JWT ou a ApiKey legada.
//  Identificadores em en-US, comentarios em pt-BR.
// ===========================================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Rochas.OpenCodeBridge;

static class Auth
{
    public static string JwtSecret = "";
    public static int JwtExpiryMinutes = 720;
    public static string UsersDbPath = "";

    public static string ResolveUsersDb()
    {
        if (UsersDbPath.Length > 0) return UsersDbPath;
        string? dir = Environment.GetEnvironmentVariable("BRIDGE_WEB_DB_DIR");
        if (!string.IsNullOrWhiteSpace(dir)) return Path.Combine(dir, "web.db");
        return Path.Combine(AppContext.BaseDirectory, "web.db");
    }

    // PBKDF2, mesmo formato do Web (iter.salt.hash em base64).
    public static bool VerifyPassword(string password, string stored)
    {
        try
        {
            string[] parts = stored.Split('.');
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expected = Convert.FromBase64String(parts[2]);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(parts[0]), HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    public static (string Name, bool IsAdmin)? Login(string email, string password)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={ResolveUsersDb()};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name, password_hash, is_admin FROM users WHERE email = @e AND active = 1 LIMIT 1;";
            cmd.Parameters.AddWithValue("@e", email.Trim().ToLowerInvariant());
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            string name = r.GetString(0);
            string hash = r.GetString(1);
            bool admin = r.GetInt64(2) == 1;
            if (!VerifyPassword(password, hash)) return null;
            return (name, admin);
        }
        catch { return null; }
    }

    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    static byte[] Hmac(byte[] key, string data)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    public static string? CreateToken(string email, bool isAdmin)
    {
        if (JwtSecret.Length < 16) return null;
        string header = B64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        long exp = DateTimeOffset.UtcNow.AddMinutes(JwtExpiryMinutes).ToUnixTimeSeconds();
        var payload = new JsonObject
        {
            ["sub"] = email,
            ["admin"] = isAdmin,
            ["exp"] = exp,
        };
        string body = B64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        string sig = B64Url(Hmac(Encoding.UTF8.GetBytes(JwtSecret), header + "." + body));
        return header + "." + body + "." + sig;
    }

    public static bool TryValidate(string token, out string email, out bool isAdmin)
    {
        email = ""; isAdmin = false;
        try
        {
            if (JwtSecret.Length < 16) return false;
            string[] parts = token.Split('.');
            if (parts.Length != 3) return false;
            byte[] expect = Hmac(Encoding.UTF8.GetBytes(JwtSecret), parts[0] + "." + parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(expect, FromB64Url(parts[2]))) return false;
            var payload = System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(FromB64Url(parts[1])))?.AsObject();
            if (payload is null) return false;
            if (payload["exp"]?.GetValue<long>() is not long exp) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp) return false;
            email = payload["sub"]?.GetValue<string>() ?? "";
            isAdmin = payload["admin"]?.GetValue<bool>() ?? false;
            return email.Length > 0;
        }
        catch { return false; }
    }
}
