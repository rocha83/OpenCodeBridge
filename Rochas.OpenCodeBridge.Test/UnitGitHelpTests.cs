using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core;
using Rochas.OpenCodeBridge.Core.Tools;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Help da tool git e' a FONTE que o modelo le (vai em todo request); o gate
/// (GitCommandSyntax.Validate) e' a rede. O help tem que listar exatamente as
/// formas que o gate aceita — as duas coisas vem do mesmo Canonical.
/// </summary>
internal static class UnitGitHelpTests
{
    static JsonObject GitTool()
    {
        var chat = ToolCatalog.Chat();
        foreach (var t in chat)
            if (t?["function"]?["name"]?.GetValue<string>() == "git") return t["function"]!.AsObject();
        throw new InvalidOperationException("tool git ausente do catalogo");
    }

    public static Task HelpListsEveryAcceptedForm()
    {
        var fn = GitTool();
        string help = (fn["description"]?.GetValue<string>() ?? "")
            + "\n" + (fn["parameters"]?["properties"]?["command"]?["description"]?.GetValue<string>() ?? "");
        foreach (string form in GitCommandSyntax.Canonical.Split(" · "))
        {
            string clean = form.Replace("`", "").Trim();
            if (clean.StartsWith("git ", StringComparison.Ordinal)) clean = clean[4..];
            int cut = clean.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0) clean = clean[..cut];
            TestContext.Check(help.Contains(clean, StringComparison.OrdinalIgnoreCase),
                $"help do git ensina a forma '{clean}'");
        }
        TestContext.Check(help.Contains("ANTES do primeiro `commit`", StringComparison.Ordinal),
            "help ensina a ordem (config antes do commit)");
        TestContext.Check(help.Contains("--path", StringComparison.Ordinal)
            && help.Contains("--bare", StringComparison.Ordinal),
            "help diz o que NAO usar (gate recusa)");
        return Task.CompletedTask;
    }

    public static Task HelpStaysShort()
    {
        var fn = GitTool();
        int chars = (fn["description"]?.GetValue<string>()?.Length ?? 0)
            + (fn["parameters"]?["properties"]?["command"]?["description"]?.GetValue<string>()?.Length ?? 0)
            + (fn["parameters"]?["properties"]?["path"]?["description"]?.GetValue<string>()?.Length ?? 0);
        // Orcamento: o array de tools viaja em TODA chamada. Antes deste ajuste
        // o total do git era ~1.751 chars; o teto impede regrowth silencioso.
        TestContext.Check(chars <= 1500, $"help do git enxuto ({chars} chars, teto 1500)");
        return Task.CompletedTask;
    }
}
