using System.Text;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// Interprete de comandos sugeridos: extrai blocos ```sh|bash do texto do modelo,
// compõe comandos linha a linha e traduz escrita (echo >/heredoc) para a tool write.
// Idiomas shell com efeito colateral (>, >>, <<, |) nunca vão ao shell: viram write
// ou voltam como erro para o modelo se adaptar. Tudo limitado pelo chamador.
public static class SuggestedCommands
{
    // (tool, argumentsJson) por linha executável.
    public sealed record Step(string Tool, string Args, string Echo);

    public static List<Step> Extract(string content, int maxLines = 12)
    {
        var steps = new List<Step>();
        if (string.IsNullOrWhiteSpace(content)) return steps;
        foreach (Match f in Regex.Matches(content, "```(?:sh|bash)[^\\n\\r]*[\\r\\n]+(.*?)```",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string body = f.Groups[1].Value;
            string[] lines = body.Split('\n');
            for (int li = 0; li < lines.Length && steps.Count < maxLines; li++)
            {
                string line = lines[li].Trim().TrimEnd('\\').Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                if (line.StartsWith("set ") || line == "set -euo pipefail") continue;
                // heredoc: cat > arq <<'EOF' ... EOF  =>  write(arq, bloco)
                var h = Regex.Match(line, "(?:cat\\s+)?>\\s*(?<f>\\S+)\\s*<<\\s*['\"]?(?<d>\\w+)['\"]?$|cat\\s*<<\\s*['\"]?(?<d2>\\w+)['\"]?\\s*>\\s*(?<f2>\\S+)\\s*$");
                if (h.Success)
                {
                    string delim = h.Groups["d"].Success ? h.Groups["d"].Value : h.Groups["d2"].Value;
                    string file = h.Groups["f"].Success ? h.Groups["f"].Value : h.Groups["f2"].Value;
                    var hb = new StringBuilder();
                    li++;
                    while (li < lines.Length && lines[li].Trim() != delim) { hb.AppendLine(lines[li]); li++; }
                    steps.Add(new Step("write",
                        "{\"path\": " + JsonEscape(file.Trim('"', '\'')) + ", \"content\": " + JsonEscape(hb.ToString()) + "}",
                        $"write {file} (heredoc)"));
                    continue;
                }
                var step = Translate(line);
                if (step is not null) steps.Add(step);
            }
            if (steps.Count > 0) break; // 1º bloco válido por turno basta
        }
        return steps;
    }

    private static Step? Translate(string line)
    {
        // heredoc de uma linha só não existe; bloco real tratado pelo extrator acima.
        // echo 'texto' > arq  =>  write(arq, texto)
        var m = Regex.Match(line, "^echo\\s+(?<t>.+?)\\s*(?<!\\})>\\s*(?<f>\\S+)\\s*$");
        if (m.Success && !line.Contains(">>") && !line.Contains("|"))
        {
            string t = m.Groups["t"].Value.Trim();
            if ((t.StartsWith("\"") && t.EndsWith("\"")) || (t.StartsWith("'") && t.EndsWith("'")))
                t = t[1..^1];
            t = t.Replace("\\n", "\n").Replace("\\t", "\t");
            string f = m.Groups["f"].Value.Trim().Trim('"', '\'');
            return new Step("write",
                "{\"path\": " + JsonEscape(f) + ", \"content\": " + JsonEscape(t) + "}",
                $"write {f}");
        }
        // demais linhas: shell decide (allowlist + metacaracteres no handler).
        return new Step("shell", "{\"command\": " + JsonEscape(line) + "}", line);
    }

    private static string JsonEscape(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append($"\\u{(int)c:04x}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
