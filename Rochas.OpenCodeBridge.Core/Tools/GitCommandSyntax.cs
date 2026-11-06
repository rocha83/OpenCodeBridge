using System;
using System.Collections.Generic;
using System.Linq;

namespace Rochas.OpenCodeBridge.Core.Tools;

/// <summary>
/// Forma canonica da tool `git`. O modelo pequeno inventa opcao que o git nao
/// tem (`--path`, `--force-include-all`) ou que nao se aplica aqui (`--no-bare`,
/// `-f` no config) e a call morre no stderr. Em vez de reparar token a token
/// (whack-a-mole), a forma invalida e' recusada com o comando exato no corpo —
/// o modelo copia o literal em vez de adivinhar.
/// </summary>
public static class GitCommandSyntax
{
    /// <summary>Formas que o agente deve usar (vai no corpo do erro).</summary>
    public const string Canonical =
        "`git init` · `git add .` (ou `add -A`) · `git commit -m \"mensagem\"` · "
        + "`git config user.name \"Nome\"` · `git config user.email voce@exemplo.com` · "
        + "`git log --oneline` (ou `-1`) · `git status`";

    // Flags aceitas por subcomando de leitura (pathspec solto tambem passa).
    static readonly Dictionary<string, HashSet<string>> ReadFlags = new(StringComparer.Ordinal)
    {
        ["status"] = new(StringComparer.Ordinal) { "--short", "-s", "--porcelain" },
        ["log"] = new(StringComparer.Ordinal) { "--oneline", "--stat", "--name-only", "--graph", "--decorate", "--short" },
        ["diff"] = new(StringComparer.Ordinal) { "--stat", "--name-only", "--cached", "--staged" },
        ["show"] = new(StringComparer.Ordinal) { "--stat", "--name-only", "--oneline" },
        ["branch"] = new(StringComparer.Ordinal) { "-a", "-v", "--list" },
        ["ls-files"] = new(StringComparer.Ordinal) { "-s", "--stage" },
        ["rev-parse"] = new(StringComparer.Ordinal) { "--short", "--verify" },
    };

    /// <summary>Null = forma valida; texto = motivo + forma canonica.</summary>
    /// <param name="argv">argv com o binario na posicao 0.</param>
    /// <param name="subIdx">Indice do subcomando em `argv`.</param>
    public static string? Validate(string[] argv, int subIdx)
    {
        if (subIdx <= 0 || subIdx >= argv.Length) return null;
        string sub = argv[subIdx];
        string[] args = argv.Skip(subIdx + 1).ToArray();
        switch (sub)
        {
            case "init":
                // `.` e -q/--quiet sao formas legais do git (medido: recusar
                // `init .` derrubou o create do E-tools-full em 2026-11-06).
                foreach (string a in args)
                    if (a != "." && a != "./" && a != "-q" && a != "--quiet")
                        return Bad(sub, a);
                return null;
            case "add":
                foreach (string a in args)
                    if (a.StartsWith("-", StringComparison.Ordinal)
                        && a != "-A" && a != "--all" && a != "-u" && a != "--update"
                        && a != "-f" && a != "--force")
                        return Bad(sub, a);
                return null;
            case "commit":
                if (args.Length == 0) return Bad(sub, "(falta -m \"mensagem\")");
                if (args[0] != "-m") return Bad(sub, args[0]);
                foreach (string a in args.Skip(1))
                    if (a.StartsWith("--", StringComparison.Ordinal)) return Bad(sub, a);
                return null;
            case "config":
            {
                // `--local` e legal (grava no repo, nao no global); qualquer
                // outra flag e' invencao (`-f`, `,path:`).
                var kv = args.Where(a => a != "--local").ToArray();
                if (kv.Length != 2) return Bad(sub, "(esperado: config user.name|user.email <valor>)");
                if (kv[0] != "user.name" && kv[0] != "user.email") return Bad(sub, kv[0]);
                if (kv[1].StartsWith("-", StringComparison.Ordinal)) return Bad(sub, kv[1]);
                foreach (string a in args)
                    if (a.StartsWith("-", StringComparison.Ordinal) && a != "--local") return Bad(sub, a);
                return null;
            }
            case "rm":
            case "mv":
            case "tag":
                foreach (string a in args)
                    if (a.StartsWith("--", StringComparison.Ordinal)) return Bad(sub, a);
                return null;
            default:
                if (!ReadFlags.TryGetValue(sub, out var flags))
                    return null;
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (!a.StartsWith("-", StringComparison.Ordinal)) continue; // pathspec
                    if (flags.Contains(a)) continue;
                    if (a.StartsWith("--format=", StringComparison.Ordinal)) continue;
                    if (a == "-n" && i + 1 < args.Length) { i++; continue; }
                    if (a.Length > 1 && a[1..].All(char.IsDigit)) continue;    // -1, -5
                    return Bad(sub, a);
                }
                return null;
        }
    }

    static string Bad(string sub, string token) =>
        ToolFeedback.Format($"forma inválida em 'git {sub}': '{token}'",
            "o git não aceita essa opção/posição (vocabulário inventado)", Canonical);
}
