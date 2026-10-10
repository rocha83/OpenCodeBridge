using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "glob": lista caminhos por padrão glob (sem shell).
public sealed class GlobToolHandler : IToolHandler
{
    public string ToolName => "glob";

    private const int MaxResults = 500;

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string pattern = obj["pattern"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(pattern)) return ToolResult.Fail("pattern é obrigatório");
        string subdir = obj["path"]?.GetValue<string>() ?? "";

        if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, subdir, out string baseDir, out string why))
            return ToolResult.Fail(why);
        if (!Directory.Exists(baseDir)) return ToolResult.Fail($"diretório não encontrado: '{subdir}'");

        // "**" é recursão (AllDirectories já desce): "src/**" vira busca "*" em src,
        // "**/*.cs" vira "*.cs" na raiz. O matcher do .NET trataria "**" como nome
        // literal de diretório e falharia.
        string pat = pattern.Trim();
        string searchDir = baseDir;
        int star = pat.IndexOf("**", StringComparison.Ordinal);
        if (star >= 0)
        {
            string extra = pat[..star].Trim().TrimEnd('/', '\\');
            string tail = pat[(star + 2)..].Trim().TrimStart('/', '\\');
            if (tail.StartsWith("*")) tail = tail[1..].TrimStart('/', '\\');
            pat = string.IsNullOrWhiteSpace(tail) ? "*" : tail;
            if (!string.IsNullOrWhiteSpace(extra) && extra.IndexOf('*') < 0)
            {
                searchDir = Path.Combine(baseDir, extra);
                if (!Directory.Exists(searchDir)) return ToolResult.Fail($"diretório não encontrado: '{subdir}{Path.DirectorySeparatorChar}{extra}'");
            }
        }

        try
        {
            string[] hits = Directory.GetFileSystemEntries(searchDir, pat, SearchOption.AllDirectories);
            string[] rel = hits
                .Select(h => Path.GetRelativePath(Path.GetFullPath(context.WorkspaceRoot), h))
                .OrderBy(h => h, StringComparer.Ordinal)
                .Take(MaxResults)
                .ToArray();
            return ToolResult.Ok(rel.Length == 0 ? "(sem resultados)" : string.Join('\n', rel));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha no glob: {ex.Message}");
        }
    }
}
