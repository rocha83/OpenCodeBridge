using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Guarda de reexecucao: depois de um FAIL, o mesmo conjunto de binarios
/// (SHA-512) nao pode rodar de novo. So um rebuild que mude o hash libera.
/// Estado em logs/.rerun-guard.json (ignorado pelo git).
/// </summary>
internal static class BinaryGuard
{
    public sealed record Binaries(string Sha512, string Paths);
    public sealed record Verdict(bool Allowed, string Reason, Binaries Current, string StatePath);

    public static string DefaultRoot
    {
        get
        {
            string? repo = Environment.GetEnvironmentVariable("REPO")
                ?? Environment.GetEnvironmentVariable("LEVEL_ROOT");
            if (!string.IsNullOrWhiteSpace(repo)) return repo!;
            string cwd = Environment.CurrentDirectory;
            if (File.Exists(Path.Combine(cwd, "Rochas.OpenCodeBridge.slnx"))) return cwd;
            return cwd;
        }
    }

    public static string DefaultStatePath(string? root = null)
        => Path.Combine(root ?? DefaultRoot, "logs", ".rerun-guard.json");

    public static string DefaultBridgeDll
        => Environment.GetEnvironmentVariable("BRIDGE_DLL")
           ?? "/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0/Rochas.OpenCodeBridge.dll";

    public static Binaries Hash(params string[] paths)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        foreach (string path in normalized)
        {
            ih.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\n"));
            if (File.Exists(path))
            {
                using var fs = File.OpenRead(path);
                byte[] buf = new byte[81920];
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    ih.AppendData(buf, 0, n);
            }
            else
            {
                ih.AppendData(Encoding.UTF8.GetBytes("<missing>\n"));
            }
        }

        return new Binaries(
            Convert.ToHexString(ih.GetHashAndReset()).ToLowerInvariant(),
            string.Join("\n", normalized));
    }

    public static Binaries ForWar(string bridgeDll, string testDll, string webTestDll)
        => Hash(bridgeDll, testDll, webTestDll);

    public static Binaries ForRunner()
    {
        var baseDir = AppContext.BaseDirectory;
        var paths = new List<string>();
        try
        {
            paths.AddRange(Directory.EnumerateFiles(baseDir, "Rochas.OpenCodeBridge*.dll", SearchOption.TopDirectoryOnly));
        }
        catch { }
        string bridge = DefaultBridgeDll;
        if (File.Exists(bridge)) paths.Add(bridge);
        if (paths.Count == 0) paths.Add(baseDir);
        return Hash(paths.ToArray());
    }

    public static Verdict Check(Binaries current, string? statePath = null)
    {
        string path = statePath ?? DefaultStatePath();
        if (!File.Exists(path)) return new Verdict(true, "sem estado anterior", current, path);

        try
        {
            using var fs = File.OpenRead(path);
            var j = JsonNode.Parse(fs)?.AsObject();
            string prev = j?["sha512"]?.GetValue<string>() ?? "";
            string at = j?["at"]?.GetValue<string>() ?? "";
            if (prev.Length > 0 && prev == current.Sha512)
                return new Verdict(false,
                    $"binarios inalterados desde o ultimo FAIL ({prev[..Math.Min(16, prev.Length)]} em {at}); rebuild necessario",
                    current, path);
            return new Verdict(true, "hash diferente do ultimo FAIL", current, path);
        }
        catch (Exception ex)
        {
            return new Verdict(false, "estado invalido: " + ex.Message, current, path);
        }
    }

    public static void RecordFailure(Binaries current, string reason, string? statePath = null)
    {
        string path = statePath ?? DefaultStatePath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var j = new JsonObject
            {
                ["sha512"] = current.Sha512,
                ["at"] = DateTime.UtcNow.ToString("O"),
                ["reason"] = reason,
                ["paths"] = current.Paths
            };
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, j.ToJsonString());
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
        catch { }
    }

    public static void Clear(string? statePath = null)
    {
        string path = statePath ?? DefaultStatePath();
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
