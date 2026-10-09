using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "read": lê arquivo de texto dentro do workspace.
public sealed class ReadToolHandler : IToolHandler
{
    public string ToolName => "read";

    private const int DefaultLimit = 2000;
    private const int MaxLines = 20000;
    private const int MaxChars = 1_000_000;

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string path = obj["path"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path é obrigatório");
        int offset = ClampInt(obj["offset"]?.GetValue<int>() ?? 0, 0, int.MaxValue);
        int limit = ClampInt(obj["limit"]?.GetValue<int>() ?? DefaultLimit, 1, MaxLines);

        if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, path, out string full, out string why))
            return ToolResult.Fail(why);
        if (!File.Exists(full)) return ToolResult.Fail($"arquivo não encontrado: '{path}'");

        try
        {
            var info = new FileInfo(full);
            if (info.Length > MaxChars) return ToolResult.Fail($"arquivo grande demais ({info.Length} bytes, teto {MaxChars})");
            string[] lines = File.ReadAllLines(full);
            string[] slice = lines.Skip(offset).Take(limit).ToArray();
            return ToolResult.Ok(string.Join('\n', slice));
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao ler '{path}': {ex.Message}");
        }
    }

    private static int ClampInt(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}
