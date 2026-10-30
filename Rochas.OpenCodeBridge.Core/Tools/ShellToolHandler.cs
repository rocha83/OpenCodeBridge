using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core.Tools;

// Handler da tool "shell": comandos de console com allowlist (lógica do Runner).
public sealed class ShellToolHandler : IToolHandler
{
    public string ToolName => "shell";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "echo", "sed", "grep", "find", "wc", "diff",
        "file", "pwd", "date", "git", "dotnet", "python", "python3", "node",
        "npm", "curl", "wget", "shell", "bash", "sh", "mkdir", "touch", "cp",
        "bc", "expr", "awk", "zip", "unzip", "printf", "ps", "top", "pgrep",
    };

    private static readonly string[] DeniedTokens =
    {
        "rm", "rmdir", "mv", "kill", "pkill", "killall", "reboot", "shutdown", "halt",
        "mkfs", "dd", "fdisk", "mount", "systemctl", "service", "crontab",
        "ssh", "scp", "chmod", "chown", "nohup", "setsid", "disown",
        "exec", "eval", "su", "mkfifo",
    };

    private static readonly string[] SudoExact =
    {
        "systemctl restart opencode-bridge.service",
        "systemctl restart opencode-bridge-cpu.service",
        "systemctl status opencode-bridge.service",
        "systemctl status opencode-bridge-cpu.service",
    };

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        // Inspetor primeiro: negação uniforme em todos os caminhos (direto,
        // cadeia, payload bash -c). Checagens estruturais vêm depois.
        string command = ShellInspector.ExtractCommand(argumentsJson);
        if (ShellInspector.Validate(command) is string whyInsp)
        {
            // Anel 3: argv barrado. Contador sempre ativo + evento no log da
            // bridge (a bateria conta o evento dentro da janela da faixa).
            Metrics.Deny("ring3", "shell", whyInsp);
            context.Log?.Invoke(new JsonObject { ["event"] = "tool-deny", ["tool"] = "shell", ["reason"] = whyInsp });
            return ToolResult.Fail(whyInsp);
        }
        var parts = SplitChain(command);
        if (parts.Count <= 1)
        {
            if (!TryBuildArgv(argumentsJson, out string[] argv, out string why))
                return WithCwd(ToolResult.Fail(why), context.WorkspaceRoot, context.WorkspaceRoot);
            if (ProjectScaffoldBlocked(argv, context.WorkspaceRoot) is string blockedSingle)
                return WithCwd(ToolResult.Fail(blockedSingle), context.WorkspaceRoot, context.WorkspaceRoot);
            if (ProjectPathBlocked(argv, context.WorkspaceRoot, context.WorkspaceRoot) is string missingProject)
                return WithCwd(ToolResult.Fail(missingProject), context.WorkspaceRoot, context.WorkspaceRoot);
            return WithCwd(ProcessRunner.Run(argv[0], argv[1..], context.WorkspaceRoot, context.TimeoutSeconds),
                context.WorkspaceRoot, context.WorkspaceRoot);
        }
        // Cadeia com semântica set -e: roda em sequência, para no 1º erro, cd persiste.
        string dir = context.WorkspaceRoot;
        var sb = new StringBuilder();
        foreach (string raw in parts)
        {
            string p = raw.Trim();
            if (p.Length == 0) continue;
            if (p == "cd" || p.StartsWith("cd "))
            {
                string target = p.Length > 2 ? p[2..].Trim().Trim('"', '\'') : ".";
                if (string.IsNullOrWhiteSpace(target)) target = ".";
                if (!WorkspaceGuard.TryResolve(dir, target, out string nd, out string whyCd, context.ExtraRoots))
                    return WithCwd(ToolResult.Fail(whyCd), dir, context.WorkspaceRoot);
                if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, nd, out _, out _, context.ExtraRoots))
                    return WithCwd(ToolResult.Fail("cd fora do workspace"), dir, context.WorkspaceRoot);
                dir = nd;
                continue;
            }
            if (!TryBuildArgvFromCommand(p, out string[] argv, out string why))
                return WithCwd(ToolResult.Fail(sb.Length > 0 ? sb.ToString() + "\n" + why : why), dir, context.WorkspaceRoot);
            if (ProjectScaffoldBlocked(argv, dir) is string blockedStep)
                return WithCwd(ToolResult.Fail(sb.Length > 0 ? sb.ToString() + "\n" + blockedStep : blockedStep), dir, context.WorkspaceRoot);
            if (ProjectPathBlocked(argv, dir, context.WorkspaceRoot) is string missingStep)
                return WithCwd(ToolResult.Fail(sb.Length > 0 ? sb.ToString() + "\n" + missingStep : missingStep), dir, context.WorkspaceRoot);
            var r = ProcessRunner.Run(argv[0], argv[1..], dir, context.TimeoutSeconds);
            if (!string.IsNullOrWhiteSpace(r.Output)) sb.AppendLine(r.Output.TrimEnd());
            if (!r.Success)
                return WithCwd(ToolResult.Fail((sb.Length > 0 ? sb.ToString() + "\n" : "") + (r.Error ?? "falha")), dir, context.WorkspaceRoot);
        }
        return WithCwd(ToolResult.Ok(sb.ToString().TrimEnd()), dir, context.WorkspaceRoot);
    }

    // O modelo precisa VER onde a chamada rodou: sem isso ele assume que o `cd`
    // persistiu entre chamadas e grava `a.txt` na raiz achando que esta na
    // subpasta. E' saida verdadeira (o diretorio efetivo), no formato que ensina
    // a forma certa do path de arquivo.
    private static ToolResult WithCwd(ToolResult r, string dir, string root)
    {
        string full, baseRoot;
        try
        {
            full = Path.GetFullPath(dir);
            baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return r; }
        string rel = full.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full[(baseRoot.Length + 1)..]
            : (full == baseRoot ? "." : full);
        var sb = new StringBuilder();
        if (r.Success && !string.IsNullOrWhiteSpace(r.Output)) sb.AppendLine(r.Output.TrimEnd());
        if (!r.Success && !string.IsNullOrWhiteSpace(r.Error)) sb.AppendLine(r.Error.TrimEnd());
        sb.Append("[shell] cwd desta chamada: " + rel);
        if (rel != ".")
            sb.Append(" | a proxima chamada comeca na raiz do workspace; arquivo desta pasta = path \"" + rel + "/arquivo\"");
        string text = sb.ToString().TrimEnd();
        return r.Success ? ToolResult.Ok(text) : ToolResult.Fail(text);
    }

    // Divide em comandos top-level por && e ; (respeita aspas e escapes \";
    // & solitário continua negado).
    private static List<string> SplitChain(string command)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '\\' && i + 1 < command.Length)
            {
                cur.Append(c);
                cur.Append(command[++i]);
                continue;
            }
            if (quote != '\0')
            {
                cur.Append(c);
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                cur.Append(c);
            }
            else if (c == ';')
            {
                parts.Add(cur.ToString());
                cur.Clear();
            }
            else if (c == '&' && i + 1 < command.Length && command[i + 1] == '&')
            {
                parts.Add(cur.ToString());
                cur.Clear();
                i++;
            }
            else cur.Append(c);
        }
        parts.Add(cur.ToString());
        return parts;
    }

    private static bool TryBuildArgv(string arguments, out string[] argv, out string why)
    {
        why = "";
        argv = Array.Empty<string>();

        // Extrai "command" quando o modelo manda JSON {"command": "..."}.
        string command = ShellInspector.ExtractCommand(arguments);
        return TryBuildArgvFromCommand(command, out argv, out why);
    }

    private static bool TryBuildArgvFromCommand(string command, out string[] argv, out string why)
    {
        why = "";
        argv = Array.Empty<string>();

        // sudo só por match exato da lista.
        if (FirstToken(command) == "sudo")
        {
            string full = command.Trim();
            if (SudoExact.Contains(full))
            {
                argv = new[] { "sudo" }.Concat(SplitArgs(command["sudo".Length..])).ToArray();
                return true;
            }
            why = "sudo fora da lista exata";
            return false;
        }

        string name = FirstToken(command);
        if (!Allowed.Contains(name))
        {
            why = $"comando '{name}' fora do allowlist";
            return false;
        }

        // shell/bash/sh executam a string via bash -c (sintaxe shell liberada).
        if (name is "shell" or "bash" or "sh")
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                why = "shell comando vazio";
                return false;
            }
            argv = new[] { "bash", "-c", command };
            return true;
        }

        // Demais comandos: sem shell, com checagens de metacaracteres e tokens.
        // Trechos entre aspas vão literais por argv (sem interpretador): só contam
        // metacaracteres fora de aspas. SplitChain já separou && e ; top-level.
        // Liberados: | e > (jail contem no workspace; negar travava o modelo em loop).
        string rest = command.Length > name.Length ? command[name.Length..] : "";
        string bare = StripQuoted(rest);
        foreach (char c in new[] { ';', '&', '<', '$', '`', '!', '\r', '\n' })
            if (bare.Contains(c))
            {
                why = $"metacaractere '{c}' negado" + HintFor(c);
                return false;
            }
        foreach (string bad in DeniedTokens)
        {
            string pattern = $@"[\s\""]{Regex.Escape(bad)}[\s\""]";
            if (Regex.IsMatch(" " + rest + " ", pattern, RegexOptions.IgnoreCase))
            {
                why = $"token '{bad}' negado";
                return false;
            }
        }

        // `>`/`|` só têm semântica via bash: no argv direto o token vira
        // argumento literal (medido: `git log ... > f` -> "ambiguous argument
        // '>'" e o modelo repete). O inspetor já validou a string inteira.
        if (bare.Contains('>') || bare.Contains('|'))
        {
            argv = new[] { "bash", "-c", command };
            return true;
        }

        argv = new[] { name }.Concat(ExpandGlobs(rest)).ToArray();
        return true;
    }

    // O modelo precisa da alternativa NA MESMA resposta: sem isso ele repete o
    // comando negado (medido no E-dual: dois denies iguais, depois 8 turnos de
    // anti-repeat sem artefato). '>' e '|' sao liberados pelo inspetor.
    static string HintFor(char c) => c switch
    {
        '$' or '`' => " — sem substituicao de comando: rode o comando e capture com 'cmd > arquivo' ou grave o texto com a tool write",
        '<' => " — sem entrada por arquivo: leia com a tool read e passe o conteudo como argumento",
        '!' => " — sem expansao de historico",
        _ => " — um comando por chamada; use && so entre passos independentes"
    };

    // `dotnet new` em pasta que JA tem projeto cria um segundo .csproj (medido:
    // mcs.csproj do scaffold + MCS.csproj do modelo; Linux e case-sensitive) e
    // `dotnet run --project <pasta>` passa a recusar a pasta. O guard devolve a
    // alternativa em vez de deixar o modelo queimar turnos.
    static string? ProjectScaffoldBlocked(string[] argv, string dir)
    {
        if (argv.Length < 2) return null;
        if (Path.GetFileName(argv[0]) != "dotnet" || argv[1] != "new") return null;
        string? output = null, name = null;
        for (int i = 2; i < argv.Length - 1; i++)
        {
            if (argv[i] is "-o" or "--output") output = argv[i + 1];
            else if (argv[i] is "-n" or "--name") name = argv[i + 1];
        }
        string target = output is not null ? Path.Combine(dir, output)
            : name is not null ? Path.Combine(dir, name)
            : dir;
        if (!Directory.Exists(target)) return null;
        string? proj = Directory.EnumerateFiles(target, "*.*proj").Select(Path.GetFileName).FirstOrDefault();
        if (proj is null) return null;
        string full = Path.IsPathRooted(target) ? target : Path.Combine(dir, target);
        return $"'{target}' ja tem projeto ({proj}): nao crie outro. Edite o fonte existente e rode " +
               $"`dotnet build --project {full}` ou `dotnet run --project {full}` " +
               $"(com mais de um .csproj, passe o arquivo: --project {Path.Combine(full, proj)}).";
    }

    // `dotnet run --project <arquivo>` com caminho que NAO existe: o console
    // devolve so' "The provided file path does not exist" e o modelo repete a
    // mesma chamada errada (medido no `E-mission-algo-cs-btree` de 2026-10-30:
    // `<ws>/mcs.csproj` em vez de `<ws>/mcs/mcs.csproj`, 5 turnos seguidos).
    // Mesma regra das tools de arquivo: a resposta traz o caminho REAL.
    static string? ProjectPathBlocked(string[] argv, string dir, string root)
    {
        string? value = null;
        for (int i = 0; i < argv.Length; i++)
        {
            if (argv[i] is "--project" or "-p" && i + 1 < argv.Length) { value = argv[i + 1]; break; }
            if (argv[i].StartsWith("--project=", StringComparison.Ordinal)) { value = argv[i]["--project=".Length..]; break; }
        }
        if (string.IsNullOrWhiteSpace(value)) return null;
        string wanted = value!;
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(wanted) ? wanted : Path.Combine(dir, wanted)); }
        catch { return null; }
        if (File.Exists(full) || Directory.Exists(full)) return null;
        string found = WorkspaceGuard.Locate(root, wanted);
        return found.Length > 0
            ? WorkspaceGuard.NotFound("projeto", wanted, found) + " (rode de novo com esse caminho)"
            : WorkspaceGuard.NotFound("projeto", wanted)
              + " (o caminho do projeto sai da raiz do workspace: '<dir>/<nome>.csproj' ou '.fsproj')";
    }

    // Devolve só o fora-de-aspas (para checagem de metacaracteres; o citado vai literal).
    private static string StripQuoted(string s)
    {
        var sb = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length) { i++; continue; }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'') quote = c;
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string FirstToken(string command)
    {
        string t = (command ?? "").TrimStart();
        int i = t.IndexOfAny(new[] { ' ', '\t' });
        return i < 0 ? t : t[..i];
    }

    private static string[] SplitArgs(string s)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        foreach (char c in s.Trim())
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; else cur.Append(c); }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c)) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } }
            else cur.Append(c);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts.ToArray();
    }

    private static string[] ExpandGlobs(string rest)
    {
        var expanded = new List<string>();
        foreach (string p in SplitArgs(rest))
        {
            if (p.IndexOfAny(new[] { '*', '?', '[' }) < 0) { expanded.Add(p); continue; }
            try
            {
                string dir = Path.GetDirectoryName(p) ?? ".";
                string absDir = Path.Combine(Directory.GetCurrentDirectory(), dir);
                string[] hits = Directory.GetFileSystemEntries(absDir, Path.GetFileName(p));
                if (hits.Length == 0) expanded.Add(p);
                else expanded.AddRange(hits.OrderBy(h => h).Select(h => Path.GetRelativePath(Directory.GetCurrentDirectory(), h)));
            }
            catch { expanded.Add(p); }
        }
        return expanded.ToArray();
    }
}
