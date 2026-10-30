using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core.Tools;

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
        if (ToolContentGuard.Elided(newString, "edit") is string elided)
            return ToolResult.Fail(elided);
        if (ToolContentGuard.WholeFileRewrite(oldString, newString) is string rewrite)
            return ToolResult.Fail(rewrite);

        if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, path, out string full, out string why, context.ExtraRoots))
            return ToolResult.Fail(why);
        if (!File.Exists(full))
        {
            // O modelo frequentemente passa so o nome depois de um `cd` no shell:
            // aponta o caminho real para a proxima chamada ser a certa.
            return ToolResult.Fail(WorkspaceGuard.NotFound("arquivo", path,
                WorkspaceGuard.Locate(context.WorkspaceRoot, path, context.ExtraRoots)));
        }

        try
        {
            string text = File.ReadAllText(full);
            if (text.Length > MaxChars) return ToolResult.Fail($"arquivo grande demais ({text.Length} chars, teto {MaxChars})");

            int count = CountOccurrences(text, oldString);
            if (count == 0)
            {
                // Nao ecoa o trecho errado: a dica e' a forma que funciona. O modelo
                // reenviava o bloco inteiro com um `oldString` que nao existia e batia no
                // mesmo erro (medido no isolado de 2026-10-29); a primeira linha real
                // serve de ancora para o trecho curto.
                string first = "";
                foreach (string line in text.Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    first = line.Trim();
                    break;
                }
                if (first.Length > 80) first = first[..80];
                return ToolResult.Fail("oldString não encontrado no arquivo. Copie o trecho EXATO de um `read` "
                    + "(um trecho curto, de uma linha, e' mais facil de acertar) ou grave o arquivo inteiro com `write`. "
                    + $"Primeira linha de '{path}': {first}");
            }
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
