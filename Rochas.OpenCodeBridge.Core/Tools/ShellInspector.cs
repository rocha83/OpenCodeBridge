using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core.Tools;

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
        "exec", "eval", "su", "mkfifo", "mv", "rmdir", "truncate", "shred",
    };

    private static readonly string[] DeniedPatterns =
    {
        // Dispositivos de bloco (descartar em /dev/null segue liberado).
        "/dev/sd", "/dev/nvme", "/dev/hd", "/dev/vd", "/dev/mmcblk", "of=/dev/", ":(){",
        // reset --hard apaga trabalho sem volta: sempre negado (sem excecao).
        "reset --hard",
        // Remocao/truncamento por API (python/node): o vetor nao e o binario rm,
        // e o nome vem colado em "." e "(" — Contains, sem fronteira de token.
        "os.remove", "os.unlink", "unlinkSync", "rmSync", "rmtree", "shutil.rmtree", "fs.unlink",
    };

    // Interpretadores com payload inline: o allowlist de 1o token nao enxerga o
    // que vai dentro, entao o payload e revalidado recursivamente (fail-closed).
    private static readonly string[] PayloadBins =
    {
        "bash", "sh", "shell", "zsh", "dash", "python", "python3", "node", "perl", "ruby",
    };

    private static readonly string[] PayloadFlags = { "-c", "-e", "-p", "--eval", "--command" };

    // Pipe direto para interpretador (curl ... | sh) nunca e inofensivo.
    private static readonly Regex PipeToShell = new(
        @"\|\s*(?:sudo\s+)?(?:bash|sh|zsh|dash|python3?|node|perl|ruby)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

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
    public static string? Validate(string command) => Validate(command, 0);

    static string? Validate(string command, int depth)
    {
        string cmd = command ?? "";
        if (string.IsNullOrWhiteSpace(cmd)) return "shell comando vazio";
        if (depth > 3) return "payload de interpretador aninhado demais: use a tool direta";
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
        // Escrita por sed in-place: fura o edit (trecho exato) e o diff.
        if (SedInPlace(cmd))
            return "sed -i (in-place) negado: use a tool edit no trecho exato";
        // Pipe para interpretador: esconde o comando real do inspetor.
        if (PipeToShell.IsMatch(cmd))
            return "pipe para interpretador negado: leia com fetch/read e rode o comando direto";
        // Payload de interpretador (bash -c "...", python -c "...", node -e "..."):
        // revalida o conteudo como se fosse o comando pedido.
        if (PayloadOf(cmd, out string? payload))
        {
            if (string.IsNullOrWhiteSpace(payload))
                return "interpretador sem payload: informe o comando ou use a tool direta";
            if (Validate(payload, depth + 1) is string inner)
                return $"dentro de {FirstToken(cmd)}: {inner}";
        }
        // top interativo trava o runner: só batch.
        if (string.Equals(FirstToken(cmd), "top", StringComparison.OrdinalIgnoreCase)
            && !Regex.IsMatch(cmd, @"(?:^|\s)-[a-zA-Z]*[bn]"))
            return "top interativo negado: use 'top -b -n 1'";
        return null;
    }

    // sed com -i/-Ei/-i.bak/--in-place grava no arquivo sem passar por write/edit.
    static bool SedInPlace(string cmd)
    {
        var m = Regex.Match(cmd, @"\bsed\b([^|;&]*)", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        foreach (string tok in m.Groups[1].Value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (tok.StartsWith("--"))
                return tok.Equals("--in-place", StringComparison.OrdinalIgnoreCase)
                    || tok.StartsWith("--in-place=", StringComparison.OrdinalIgnoreCase);
            if (!tok.StartsWith("-")) return false;   // primeiro operando: acabaram as flags
            if (tok[1..].Contains('i')) return true;  // -i, -Ei, -ni, -i.bak
        }
        return false;
    }

    // Detecta interpretador com payload inline e devolve o payload entre aspas.
    static bool PayloadOf(string cmd, out string? payload)
    {
        payload = null;
        string t = (cmd ?? "").TrimStart();
        int sp = t.IndexOfAny(new[] { ' ', '\t' });
        if (sp <= 0) return false;
        string bin = t[..sp];
        if (!PayloadBins.Any(b => string.Equals(b, bin, StringComparison.OrdinalIgnoreCase))) return false;
        var toks = Tokenize(t[(sp + 1)..]);
        for (int i = 0; i < toks.Count; i++)
        {
            string tok = toks[i];
            if (!tok.StartsWith("-")) return false;   // script/operando: sem payload inline
            bool inline = PayloadFlags.Any(f => string.Equals(f, tok, StringComparison.OrdinalIgnoreCase))
                || (tok.Length > 1 && !tok.StartsWith("--")
                    && (tok[1..].Contains('c') || tok[1..].Contains('e')));
            if (!inline) continue;
            payload = i + 1 < toks.Count ? toks[i + 1] : "";
            return true;
        }
        return false;
    }

    // Tokeniza respeitando aspas simples/duplas (o payload volta sem as aspas).
    static List<string> Tokenize(string s)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) { quote = '\0'; continue; }
                if (c == '\\' && i + 1 < s.Length && quote == '"') { sb.Append(s[++i]); continue; }
                sb.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
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
