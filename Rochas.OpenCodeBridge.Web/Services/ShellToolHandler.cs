using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "shell": comandos de console com allowlist (lógica do Runner).
public sealed class ShellToolHandler : IToolHandler
{
    public string ToolName => "shell";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "echo", "sed", "grep", "find", "wc", "diff",
        "file", "pwd", "date", "git", "dotnet", "python", "python3", "node",
        "npm", "curl", "shell", "bash", "sh",
    };

    private static readonly string[] DeniedTokens =
    {
        "rm", "kill", "pkill", "killall", "reboot", "shutdown", "halt",
        "mkfs", "dd", "fdisk", "mount", "systemctl", "service", "crontab",
        "ssh", "scp", "wget", "chmod", "chown", "nohup", "setsid", "disown",
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
        if (!TryBuildArgv(argumentsJson, out string[] argv, out string why))
            return ToolResult.Fail(why);
        return ProcessRunner.Run(argv[0], argv[1..], context.WorkspaceRoot, context.TimeoutSeconds);
    }

    private static bool TryBuildArgv(string arguments, out string[] argv, out string why)
    {
        why = "";
        argv = Array.Empty<string>();

        // Extrai "command" quando o modelo manda JSON {"command": "..."}.
        string command = ExtractCommand(arguments);

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
        string rest = command.Length > name.Length ? command[name.Length..] : "";
        foreach (char c in new[] { ';', '&', '|', '>', '<', '$', '`', '!', '\r', '\n' })
            if (rest.Contains(c))
            {
                why = $"metacaractere '{c}' negado";
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

        argv = new[] { name }.Concat(ExpandGlobs(rest)).ToArray();
        return true;
    }

    private static string ExtractCommand(string arguments)
    {
        try
        {
            var obj = JsonNode.Parse(arguments)?.AsObject();
            if (obj?["command"]?.GetValue<string>() is string cmd && !string.IsNullOrWhiteSpace(cmd))
                return cmd.Trim();
        }
        catch { /* não é JSON: usa cru */ }
        return (arguments ?? "").Trim();
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
