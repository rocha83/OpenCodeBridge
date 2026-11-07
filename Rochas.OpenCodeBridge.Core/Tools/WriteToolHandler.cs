using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core.Tools;

// Handler da tool "write": escreve arquivo (cria diretórios pais).
public sealed class WriteToolHandler : IToolHandler
{
    public string ToolName => "write";

    private const int MaxChars = 1_000_000;

    // Extensoes em que uma cerca de markdown no conteudo quebra o arquivo.
    private static readonly HashSet<string> CodeExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".fs", ".vb", ".py", ".ts", ".tsx", ".js", ".jsx",
        ".json", ".sql", ".sh", ".bash", ".java", ".go", ".rs", ".c", ".h", ".cpp",
        ".hpp", ".css", ".html", ".xml", ".yml", ".yaml"
    };

    // O E4B as vezes devolve o arquivo inteiro em cerca de markdown:
    //   ```csharp
    //   Console.WriteLine("x");
    //   ```
    // e o .cs nao compila (CS1056, medido no L2 114217). Para extensao de codigo,
    // remove 1 cerca externa; em .md/.txt o conteudo fica intacto.
    private static string StripCodeFence(string content, string path)
    {
        if (!CodeExt.Contains(Path.GetExtension(path))) return content;
        string t = content.Trim('\uFEFF', ' ', '\t', '\r', '\n');
        if (!t.StartsWith("```", StringComparison.Ordinal)) return content;
        int nl = t.IndexOf('\n');
        if (nl < 0) return content;
        string open = t[..nl].Trim();
        if (open.Length < 3 || open.TrimStart('`').Length > 20) return content;
        string body = t[(nl + 1)..];
        int close = body.LastIndexOf("```", StringComparison.Ordinal);
        if (close < 0 || body[(close + 3)..].Trim().Length > 0) return content;
        return body[..close].TrimEnd() + "\n";
    }

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string path = obj["path"]?.GetValue<string>() ?? "";
        string rawContent = obj["content"]?.GetValue<string>() ?? "";
        string content = StripCodeFence(rawContent, path);
        if (!ReferenceEquals(content, rawContent))
            context.Log?.Invoke(new JsonObject
            {
                ["event"] = "write_repair", ["applied"] = "code_fence", ["path"] = path,
                ["chars_before"] = rawContent.Length, ["chars_after"] = content.Length
            });
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path é obrigatório");
        if (content.Length > MaxChars) return ToolResult.Fail($"conteúdo grande demais ({content.Length} chars, teto {MaxChars})");
        if (ToolContentGuard.Elided(content, "write") is string elided)
            return ToolResult.Fail(elided);
        if (RepoPathGuard.GitInternal(path, "write") is string inGit)
        {
            Metrics.CountToolDenial("ring2", "write", "dotgit");
            return ToolResult.Fail(inGit);
        }

        if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, path, out string full, out string why, context.ExtraRoots))
            return ToolResult.Fail(why);

        try
        {
            string? dir = Path.GetDirectoryName(full);
            string? created = ArtifactOwnership.FirstMissingDir(dir);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            bool existed = File.Exists(full);
            File.WriteAllText(full, content);
            // Bridge como root: o artefato (e os diretorios criados aqui) herdam o
            // dono do workspace, para o cliente poder editar/rebuildar o que a tool
            // escreveu (medido em E-front-edit, 2026-10-30).
            ArtifactOwnership.Adopt(full, context.Log, created);
            return ToolResult.Ok($"{(existed ? "atualizado" : "criado")} {content.Length} chars em '{path}'");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao escrever '{path}': {ex.Message}");
        }
    }
}
