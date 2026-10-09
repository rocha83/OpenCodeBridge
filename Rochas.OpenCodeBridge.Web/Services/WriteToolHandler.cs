using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "write": escreve arquivo (cria diretórios pais).
public sealed class WriteToolHandler : IToolHandler
{
    public string ToolName => "write";

    private const int MaxChars = 1_000_000;

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string path = obj["path"]?.GetValue<string>() ?? "";
        string content = obj["content"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path é obrigatório");
        if (content.Length > MaxChars) return ToolResult.Fail($"conteúdo grande demais ({content.Length} chars, teto {MaxChars})");

        if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, path, out string full, out string why))
            return ToolResult.Fail(why);

        try
        {
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, content);
            return ToolResult.Ok($"escrito {content.Length} chars em '{path}'");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao escrever '{path}': {ex.Message}");
        }
    }
}
