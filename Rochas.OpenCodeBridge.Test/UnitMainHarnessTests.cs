using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Unit do main harness: texto unico que substitui o system do cliente no
// caminho gerido (sem sufixo de role). Sem HTTP e sem modelo.
internal static class UnitMainHarnessTests
{
    static string Text => global::Rochas.OpenCodeBridge.Core.MainHarness.Text;

    public static Task NoSandboxInstructions()
    {
        TestContext.Check(!Text.Contains("# Code Mode", StringComparison.OrdinalIgnoreCase), "sem Code Mode");
        TestContext.Check(!Text.Contains("cannot be called directly", StringComparison.OrdinalIgnoreCase), "sem chamada indireta");
        TestContext.Check(!Text.Contains("Use the `execute` tool", StringComparison.OrdinalIgnoreCase), "sem execute obrigatorio");
        TestContext.Check(Text.Contains("execute{tools.X}", StringComparison.OrdinalIgnoreCase), "explica o unwrap do execute");
        TestContext.Check(Text.Contains("sandbox", StringComparison.OrdinalIgnoreCase), "declara ausencia de sandbox");
        return Task.CompletedTask;
    }

    public static Task CompleteToolUsage()
    {
        foreach (string tool in new[]
                 {
                     "read", "write", "edit", "glob", "grep", "shell", "skill",
                     "subagent", "webfetch", "websearch", "question"
                 })
            TestContext.Check(Text.Contains(tool, StringComparison.OrdinalIgnoreCase), "main harness cita " + tool);
        return Task.CompletedTask;
    }

    public static Task KeepsSkillsAndEnv()
    {
        TestContext.Check(Text.Contains("<available_skills>", StringComparison.OrdinalIgnoreCase), "main harness fala de skills");
        TestContext.Check(Text.Contains("<env>", StringComparison.OrdinalIgnoreCase), "main harness fala de env");
        TestContext.Check(Text.Contains("AMBIENTE", StringComparison.OrdinalIgnoreCase), "main harness tem ambiente");
        return Task.CompletedTask;
    }

    public static Task HasMoeSection()
    {
        TestContext.Check(Text.Contains("MULTI-SEGMENTO", StringComparison.OrdinalIgnoreCase), "main harness tem secao MoE");
        foreach (string area in new[] { "filosófico", "científico", "médico", "jurídico", "consultor" })
            TestContext.Check(Text.Contains(area, StringComparison.OrdinalIgnoreCase), "MoE cobre " + area);
        return Task.CompletedTask;
    }

    public static Task IsCompact()
    {
        int n = Text.Length;
        TestContext.Check(n is > 2500 and < 12500, "main harness compacto (" + n + " chars)");
        return Task.CompletedTask;
    }

    public static Task ReplacesManagedSystem()
    {
        var messages = new JsonArray(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "Voce e um backend senior. Respostas curtas."
        });
        bool applied = global::Rochas.OpenCodeBridge.Program.ApplyMainHarness(messages, hasProfile: false, enabled: true);
        TestContext.Check(applied, "aplica no caminho gerido");
        string? content = messages[0]?["content"]?.GetValue<string>();
        TestContext.Check(content?.StartsWith(global::Rochas.OpenCodeBridge.Core.MainHarness.Text) == true,
            "system substituido pelo main harness");
        TestContext.Check(content?.Contains("INSTRUCOES DO CLIENTE") == true, "preserva instrucoes pequenas");
        return Task.CompletedTask;
    }

    public static Task KeepsProfileSystem()
    {
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "cliente" });
        bool applied = global::Rochas.OpenCodeBridge.Program.ApplyMainHarness(messages, hasProfile: true, enabled: true);
        TestContext.Check(!applied, "nao aplica no opencode");
        TestContext.Check(messages[0]?["content"]?.GetValue<string>() == "cliente", "system do opencode intacto");
        return Task.CompletedTask;
    }

    public static Task KeepsOpencodeMasterWithoutProfile()
    {
        var messages = new JsonArray(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "# Code Mode\nUse the execute tool."
        });
        bool applied = global::Rochas.OpenCodeBridge.Program.ApplyMainHarness(messages, hasProfile: false, enabled: true);
        TestContext.Check(!applied, "nao aplica no master opencode sem sufixo");
        TestContext.Check(messages[0]?["content"]?.GetValue<string>()?.Contains("# Code Mode") == true,
            "system do opencode intacto");
        return Task.CompletedTask;
    }

    public static Task PreservesDynamicBlocks()
    {
        string client = "Today's date: Sat Oct 24 2026\n<env>\n  Working directory: /tmp/x\n</env>\n<available_skills>\n  <skill><id>opencode</id></skill>\n</available_skills>";
        string built = global::Rochas.OpenCodeBridge.Program.BuildMainHarnessSystem(client);
        TestContext.Check(built.Contains("Working directory: /tmp/x"), "preserva env");
        TestContext.Check(built.Contains("<available_skills>"), "preserva skills");
        TestContext.Check(built.Contains("Today's date:"), "preserva data");
        TestContext.Check(built.Contains("INSTRUCOES DO CLIENTE"), "preserva system pequeno");
        return Task.CompletedTask;
    }

    public static Task DropsMasterButKeepsEnv()
    {
        string master = "# Code Mode\nUse the execute tool.\n<env>\n  Working directory: /tmp/x\n</env>\n<available_skills>\n  <skill><id>opencode</id></skill>\n</available_skills>";
        string built = global::Rochas.OpenCodeBridge.Program.BuildMainHarnessSystem(master);
        TestContext.Check(!built.Contains("# Code Mode"), "nao copia Code Mode");
        TestContext.Check(built.Contains("Working directory: /tmp/x"), "preserva env do master");
        TestContext.Check(!built.Contains("INSTRUCOES DO CLIENTE"), "nao copia master como instrucao");
        return Task.CompletedTask;
    }
}
