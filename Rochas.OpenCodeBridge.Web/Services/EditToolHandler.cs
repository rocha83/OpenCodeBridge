using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "edit": substitui texto exato (único por padrão).
public sealed class EditToolHandler : IToolHandler
{
    public string ToolName => "edit";

    private const int MaxChars = 1_000_000;

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string path = obj["path"]?.GetValue<string>() ?? "";
        string oldString = obj["oldString"]?.GetValue<string>() ?? "";
        string newString = obj["newString"]?.GetValue<string>() ?? "";
        bool replaceAll = obj["replaceAll"]?.GetValue<bool>() ?? false;
        if (string.IsNullOrWhiteSpace(path)) return ToolResult.Fail("path é obrigatório");
        if (string.IsNullOrEmpty(oldString)) return ToolResult.Fail("oldString é obrigatório");

        if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, path, out string full, out string why))
            return ToolResult.Fail(why);
        if (!File.Exists(full)) return ToolResult.Fail($"arquivo não encontrado: '{path}'");

        try
        {
            string text = File.ReadAllText(full);
            if (text.Length > MaxChars) return ToolResult.Fail($"arquivo grande demais ({text.Length} chars, teto {MaxChars})");

            int count = CountOccurrences(text, oldString);
            if (count == 0) return ToolResult.Fail("oldString não encontrado no arquivo");
            if (count > 1 && !replaceAll) return ToolResult.Fail($"oldString ocorre {count}x; use replaceAll ou refine o trecho");

            string updated = replaceAll ? text.Replace(oldString, newString) : ReplaceFirst(text, oldString, newString);
            File.WriteAllText(full, updated);
            return ToolResult.Ok(replaceAll ? $"{count} substituições em '{path}'" : $"1 substituição em '{path}'");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao editar '{path}': {ex.Message}");
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, i = 0;
        while ((i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0) { count++; i += value.Length; }
        return count;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        int i = text.IndexOf(oldValue, StringComparison.Ordinal);
        return i < 0 ? text : text[..i] + newValue + text[(i + oldValue.Length)..];
    }
}
