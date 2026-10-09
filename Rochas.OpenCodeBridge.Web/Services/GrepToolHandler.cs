using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "grep": busca regex em arquivos (sem shell).
public sealed class GrepToolHandler : IToolHandler
{
    public string ToolName => "grep";

    private const int MaxMatches = 100;
    private const long MaxFileBytes = 1_000_000;

    private static readonly string[] SkipDirs = { ".git", "bin", "obj", "node_modules", ".vs", "TestResults" };

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string pattern = obj["pattern"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(pattern)) return ToolResult.Fail("pattern é obrigatório");
        string subdir = obj["path"]?.GetValue<string>() ?? "";
        string include = obj["include"]?.GetValue<string>() ?? "*";

        Regex regex;
        try { regex = new Regex(pattern, RegexOptions.Compiled); }
        catch (Exception ex) { return ToolResult.Fail($"regex inválida: {ex.Message}"); }

        if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, subdir, out string baseDir, out string why))
            return ToolResult.Fail(why);
        if (!Directory.Exists(baseDir)) return ToolResult.Fail($"diretório não encontrado: '{subdir}'");

        try
        {
            var found = new List<string>();
            foreach (string file in EnumerateFiles(baseDir))
            {
                if (found.Count >= MaxMatches) break;
                if (!MatchGlob(Path.GetFileName(file), include)) continue;
                if (new FileInfo(file).Length > MaxFileBytes) continue;
                int lineNo = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNo++;
                    if (lineNo > 20000) break;
                    if (regex.IsMatch(line))
                    {
                        found.Add($"{Relative(context.WorkspaceRoot, file)}:{lineNo}: {line.Trim()}");
                        if (found.Count >= MaxMatches) break;
                    }
                }
            }
            return ToolResult.Ok(found.Count == 0 ? "(sem resultados)" : string.Join('\n', found));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha na busca: {ex.Message}");
        }
    }

    private static IEnumerable<string> EnumerateFiles(string baseDir)
    {
        var stack = new Stack<string>();
        stack.Push(baseDir);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] subs = Array.Empty<string>(), files = Array.Empty<string>();
            try
            {
                subs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch { continue; }
            foreach (string f in files) yield return f;
            foreach (string s in subs)
                if (!SkipDirs.Contains(Path.GetFileName(s), StringComparer.OrdinalIgnoreCase))
                    stack.Push(s);
        }
    }

    private static bool MatchGlob(string fileName, string include)
    {
        if (string.IsNullOrEmpty(include) || include == "*") return true;
        foreach (string part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string rx = "^" + Regex.Escape(part.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
            if (Regex.IsMatch(fileName, rx, RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    private static string Relative(string root, string full) =>
        Path.GetRelativePath(Path.GetFullPath(root), full);
}
