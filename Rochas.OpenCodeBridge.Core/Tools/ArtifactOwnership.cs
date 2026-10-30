using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core.Tools;

// A bridge roda como root (units systemd `User=root`) e as tools gravam DENTRO do
// workspace do cliente, cujo dono e' outro usuario (mint). Sem esta adocao o
// arquivo/diretorio nasce `root:root` e o dono do workspace perde o direito de
// sobrescrever o proprio artefato.
//
// Caso medido (2026-10-30, L4 / front-bridge): o `write` do modelo criou
// `/tmp/opencode/frontpipe/app/wwwroot/css/app.css` como root; no turno seguinte o
// fixture do teste (rodando como mint) fez `File.Copy(gen, target, true)` e morreu
// com "Access to the path '.../app.css' is denied." — a bateria parou no
// `E-front-edit` por permissao, nao por defeito do modelo.
//
// Regra: TODO artefato de tool herda o dono do diretorio que ja existia (o
// workspace do cliente). Sem root a chamada e' no-op (nada a corrigir quando quem
// escreve e' o proprio dono).
public static class ArtifactOwnership
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    private static readonly Lazy<bool> Root = new(() =>
    {
        try { return GetEffectiveUid() == 0; }
        catch (Exception) { return false; } // sem libc (Windows/CI): nunca e' root
    });

    /// <summary>Processo roda como root (a adocao de dono so faz sentido assim).</summary>
    public static bool IsRoot => Root.Value;

    /// <summary>
    /// Primeiro diretorio da cadeia que AINDA NAO existe (o que a tool vai criar).
    /// Caminho ja completo devolve null. Serve para saber o que foi criado pela
    /// propria chamada e precisa ser adotado junto com o arquivo.
    /// </summary>
    public static string? FirstMissingDir(string? dir)
    {
        string? top = null;
        for (string? d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(d)) break;
            top = d;
        }
        return top;
    }

    /// <summary>
    /// Ancestral existente mais proximo (referencia de dono). Null quando nao ha
    /// nenhum diretorio existente na cadeia (caminho solto, nada a referenciar).
    /// </summary>
    public static string? OwnerDir(string fullPath)
    {
        for (string? d = Path.GetDirectoryName(fullPath); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (Directory.Exists(d)) return d;
        return null;
    }

    /// <summary>
    /// Adota o arquivo (e o diretorio criado pela chamada, quando houver) com o dono
    /// do ancestral existente. Melhor esforco: falha de chown nao derruba a tool —
    /// o artefato existe e a mensagem de erro do sistema continua honesta.
    /// </summary>
    public static void Adopt(string fullPath, Action<JsonObject>? log = null, string? createdRoot = null)
    {
        if (!IsRoot) return;
        string? reference = OwnerDir(createdRoot ?? fullPath);
        if (reference is null) return;

        if (!string.IsNullOrEmpty(createdRoot) && Directory.Exists(createdRoot))
            Chown(new[] { "-R", "--reference=" + reference, createdRoot }, reference, log, createdRoot);
        if (File.Exists(fullPath))
            Chown(new[] { "--reference=" + reference, fullPath }, reference, log, fullPath);
    }

    private static void Chown(string[] args, string workingDirectory, Action<JsonObject>? log, string target)
    {
        string exe = File.Exists("/usr/bin/chown") ? "/usr/bin/chown" : "chown";
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process? proc = Process.Start(psi);
            if (proc is null) return;
            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            log?.Invoke(new JsonObject
            {
                ["event"] = "artifact-owner",
                ["path"] = target,
                ["ok"] = proc.ExitCode == 0,
                ["detail"] = (output ?? "").Trim()
            });
        }
        catch (Exception ex)
        {
            log?.Invoke(new JsonObject
            {
                ["event"] = "artifact-owner",
                ["path"] = target,
                ["ok"] = false,
                ["detail"] = ex.Message
            });
        }
    }
}
