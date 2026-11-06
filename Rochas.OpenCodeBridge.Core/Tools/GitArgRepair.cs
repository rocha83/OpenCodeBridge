using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rochas.OpenCodeBridge.Core.Tools;

/// <summary>
/// Normaliza os argumentos da tool `git` antes de executar. O modelo pequeno
/// erra o vocabulario (nao o passo): manda `--bare`, usa `.git` como `path` ou
/// copia o caminho que apareceu no erro. Sao defeitos objetivos de argumento —
/// corrigidos sem heuristica de conteudo, e call valida passa intacta.
/// </summary>
public static class GitArgRepair
{
    /// <summary>Normaliza `command`/`path`. `Applied` vazio = nada foi tocado.</summary>
    /// <param name="command">Comando cru do modelo (ex.: "git init --bare .").</param>
    /// <param name="path">Pasta do repo informada no `path` (pode ser vazia).</param>
    /// <param name="workspaceRoot">Raiz do workspace (resolve absoluto/eco de cache).</param>
    /// <returns>Comando, path e as regras aplicadas (para o log `git_repair`).</returns>
    public static (string Command, string Path, IReadOnlyList<string> Applied) Repair(
        string command, string? path, string workspaceRoot)
    {
        var applied = new List<string>();
        string cmd = NormalizeCommand(command ?? "", applied);
        bool isInit = cmd.Equals("init", StringComparison.OrdinalIgnoreCase)
                      || cmd.StartsWith("init ", StringComparison.Ordinal);
        string p = NormalizePath(path ?? "", workspaceRoot ?? "", isInit, applied);
        return (cmd, p, applied);
    }

    // `git ` no inicio e ruido (o handler ja roda o binario git).
    static string NormalizeCommand(string raw, List<string> applied)
    {
        string cmd = raw.Trim();
        if (cmd.StartsWith("git ", StringComparison.OrdinalIgnoreCase))
        {
            cmd = cmd[4..].Trim();
            applied.Add("prefix-git");
        }
        string[] tokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool isInit = tokens.Length > 0 && tokens[0].Equals("init", StringComparison.OrdinalIgnoreCase);
        var keep = new List<string>();
        for (int i = 0; i < tokens.Length; i++)
        {
            string t = tokens[i];
            if (t == "--bare")
            {
                applied.Add("drop-bare");
                continue;
            }
            // Sujeira observada em campo: `--local,path=./x` / `--local,path:"."`.
            if (t.Contains(",path=", StringComparison.Ordinal) || t.Contains(",path:", StringComparison.Ordinal))
            {
                applied.Add("drop-junk");
                continue;
            }
            // `init .git` criaria .git/.git; o repo e' o proprio workspace.
            if (isInit && i > 0 && t.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                applied.Add("drop-dotgit-arg");
                continue;
            }
            keep.Add(t);
        }
        return string.Join(' ', keep);
    }

    static string NormalizePath(string raw, string workspaceRoot, bool isInit, List<string> applied)
    {
        string path = raw.Trim();
        if (path.Length == 0) return path;
        // Diretorio interno da tool no workspace — nunca e' repo.
        if (path.Contains("node_modules/.cache/", StringComparison.Ordinal))
        {
            applied.Add("path-cache");
            return "./";
        }
        string clean = path.TrimEnd('/');
        if (clean.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^4].TrimEnd('/');
            applied.Add("path-dotgit");
        }
        if (clean.Length == 0 || clean == ".") return "./";
        if (Path.IsPathRooted(clean) && workspaceRoot.Length > 0)
        {
            string full = Path.GetFullPath(clean);
            string root = Path.GetFullPath(workspaceRoot).TrimEnd('/');
            if (full.Equals(root, StringComparison.Ordinal))
            {
                applied.Add("path-absolute");
                return "./";
            }
            if (full.StartsWith(root + "/", StringComparison.Ordinal))
            {
                applied.Add("path-absolute");
                return "./" + full[(root.Length + 1)..];
            }
        }
        // Repo desta tool = o workspace: `init` em subpasta RELATIVA inventada
        // nao e' o pedido (medido: `./repo-temp/data`, `./toolsfull-agent/...`).
        // Caminho absoluto e' do cliente (teste/Web) e continua valendo.
        if (isInit && clean != "./" && !Path.IsPathRooted(clean))
        {
            applied.Add("path-init");
            return "./";
        }
        return clean;
    }
}
