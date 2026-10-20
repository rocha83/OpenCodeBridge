using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.AI.Manager.Application.Tools;

// Inspetor pré-execução do shell: negação uniforme (comando direto, cadeia
// &&/;, payload bash -c — caminho que pulava as checagens do handler) +
// classificação p/ gates de permissão (proc/exec). Fail-closed: dúvida nega
// com motivo e o modelo reformula na retry.
public static class ShellInspector
{
    public enum ShellClass { Other, Validation, Execution, Proc, Install }

    // rm/kill/pkill + demais com prejuízo; mv/rmdir negados, só cp liberado.
    // Vale na string integral (inclusive dentro de bash -c); fronteiras de
    // palavra evitam falsos ("firmware", "subject").
    private static readonly string[] DeniedTokens =
    {
        "rm", "kill", "pkill", "killall", "reboot", "shutdown", "halt",
        "mkfs", "dd", "fdisk", "mount", "systemctl", "service", "crontab",
        "ssh", "scp", "chmod", "chown", "nohup", "setsid", "disown",
        "exec", "eval", "su", "mkfifo", "mv", "rmdir",
    };

    private static readonly string[] DeniedPatterns =
    {
        // Dispositivos de bloco (descartar em /dev/null segue liberado).
        "/dev/sd", "/dev/nvme", "/dev/hd", "/dev/vd", "/dev/mmcblk", "of=/dev/", ":(){",
        // reset --hard apaga trabalho sem volta: sempre negado (sem excecao).
        "reset --hard",
    };

    public static string ExtractCommand(string argumentsJson)
    {
        try
        {
            var obj = JsonNode.Parse(argumentsJson)?.AsObject();
            if (obj?["command"]?.GetValue<string>() is string cmd && !string.IsNullOrWhiteSpace(cmd))
                return cmd.Trim();
        }
        catch { /* não é JSON: usa cru */ }
        return (argumentsJson ?? "").Trim();
    }

    public static string FirstToken(string command)
    {
        string t = (command ?? "").TrimStart();
        int i = t.IndexOfAny(new[] { ' ', '\t' });
        return i < 0 ? t : t[..i];
    }

    // null = liberado; texto = motivo da negação.
    public static string? Validate(string command)
    {
        string cmd = command ?? "";
        if (string.IsNullOrWhiteSpace(cmd)) return "shell comando vazio";
        string padded = " " + cmd + " ";
        foreach (string bad in DeniedTokens)
        {
            string pattern = $@"[\s\""'(/|;&=]{Regex.Escape(bad)}[\s\""' )/|;&=]";
            if (Regex.IsMatch(padded, pattern, RegexOptions.IgnoreCase))
                return $"token '{bad}' negado pelo inspetor";
        }
        foreach (string pat in DeniedPatterns)
            if (cmd.Contains(pat, StringComparison.Ordinal))
                return $"padrão '{pat}' negado pelo inspetor";
        // reset --hard com espacos variados (o Contains acima pega o exato).
        if (Regex.IsMatch(cmd, @"\breset\s+--hard\b", RegexOptions.IgnoreCase))
            return "git reset --hard negado: apaga trabalho sem volta";
        // top interativo trava o runner: só batch.
        if (string.Equals(FirstToken(cmd), "top", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(cmd, @"(?:^|\s)-[a-zA-Z]*[bn]"))
            return "top interativo negado: use 'top -b -n 1'";
        return null;
    }

    public static ShellClass Classify(string command)
    {
        string first = FirstToken(command);
        if (first is "ps" or "top" or "pgrep") return ShellClass.Proc;
        if (first is "pip" or "pip3" or "apt" or "apt-get") return ShellClass.Install;
        if (first is "bash" or "sh" or "shell")
            return Regex.IsMatch(command ?? "", @"(?:^|\s)-n(?:\s|$)")
                ? ShellClass.Validation
                : ShellClass.Execution;
        return ShellClass.Other;
    }
}
