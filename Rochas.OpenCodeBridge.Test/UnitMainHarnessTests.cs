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

    public static Task CoreToolsOnly()
    {
        // O schema de cada tool vai no request: a base NAO descreve tool por tool,
        // so fixa as operacionais e declara o schema como fonte.
        TestContext.Check(Text.Contains("schema JSON de cada tool", StringComparison.OrdinalIgnoreCase),
            "base declara o schema como fonte");
        foreach (string tool in new[] { "read", "write", "edit", "glob", "grep", "shell" })
            TestContext.Check(Text.Contains(tool, StringComparison.OrdinalIgnoreCase), "base cita " + tool);
        return Task.CompletedTask;
    }

    public static Task KeepsSkillsAndEnv()
    {
        TestContext.Check(Text.Contains("<available_skills>", StringComparison.OrdinalIgnoreCase), "main harness fala de skills");
        TestContext.Check(Text.Contains("<env>", StringComparison.OrdinalIgnoreCase), "main harness fala de env");
        TestContext.Check(Text.Contains("AMBIENTE", StringComparison.OrdinalIgnoreCase), "main harness tem ambiente");
        return Task.CompletedTask;
    }

    public static Task BaseHasNoRoleSections()
    {
        // MoE, roles e pipeline sao papel do agente + politica (LAB-SETUP 4.6);
        // nao voltam para a base (evita atuacao espalhada).
        TestContext.Check(!Text.Contains("MULTI-SEGMENTO", StringComparison.OrdinalIgnoreCase), "base sem MoE");
        TestContext.Check(!Text.Contains("ROLES E PIPELINE", StringComparison.OrdinalIgnoreCase), "base sem roles/pipeline");
        return Task.CompletedTask;
    }

    public static Task IsCompact()
    {
        int n = Text.Length;
        TestContext.Check(n is > 2000 and < 6000, "main harness enxuto (" + n + " chars)");
        return Task.CompletedTask;
    }

    public static Task ReplacesManagedSystem()
    {
        var messages = new JsonArray(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "Voce e um backend senior. Respostas curtas."
        });
        bool applied = global::Rochas.OpenCodeBridge.Core.MainHarness.Apply(messages, enabled: true);
        TestContext.Check(applied, "aplica no caminho gerido");
        string? content = messages[0]?["content"]?.GetValue<string>();
        TestContext.Check(content?.StartsWith(global::Rochas.OpenCodeBridge.Core.MainHarness.Text) == true,
            "system substituido pelo main harness");
        TestContext.Check(content?.Contains("INSTRUCOES DO CLIENTE") == true, "preserva instrucoes pequenas");
        return Task.CompletedTask;
    }

    public static Task AppliesEvenWithRoleSuffix()
    {
        // Discriminador e o CLIENTE, nao o sufixo do modelo: o WAR usa
        // <modelo>-build e antes ficava sem base (hasProfile bloqueava).
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = "Voce e build." });
        bool applied = global::Rochas.OpenCodeBridge.Core.MainHarness.Apply(messages, enabled: true);
        TestContext.Check(applied, "aplica mesmo com modelo de role (cliente gerido)");
        TestContext.Check(messages[0]?["content"]?.GetValue<string>()?.StartsWith(Text) == true,
            "system substituido pela base");
        return Task.CompletedTask;
    }

    public static Task KeepsOpencodeMasterWithoutProfile()
    {
        // Determinacao vigente: em bridge lane o harness e SEMPRE on. O
        // discriminador e o modo/cliente, nao o conteudo do system. Master
        // curto vira INSTRUCOES DO CLIENTE, sem substituir a base.
        var messages = new JsonArray(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "# Code Mode\nUse the execute tool."
        });
        bool applied = global::Rochas.OpenCodeBridge.Core.MainHarness.Apply(messages, enabled: true);
        TestContext.Check(applied, "aplica mesmo com master opencode em bridge lane");
        string? content = messages[0]?["content"]?.GetValue<string>();
        TestContext.Check(content?.StartsWith(Text) == true, "base do harness prevalece");
        TestContext.Check(content?.Contains("INSTRUCOES DO CLIENTE") == true,
            "master curto vira instrucao do cliente");

        // Raia opencode (enabled=false) segue intacta: o harness dela vem do
        // proprio opencode no stream com o llama.
        var opencode = new JsonArray(new JsonObject
        {
            ["role"] = "system",
            ["content"] = "# Code Mode\nUse the execute tool."
        });
        bool off = global::Rochas.OpenCodeBridge.Core.MainHarness.Apply(opencode, enabled: false);
        TestContext.Check(!off, "raia opencode nao recebe o nosso harness");
        TestContext.Check(opencode[0]?["content"]?.GetValue<string>()?.Contains("# Code Mode") == true,
            "system do opencode intacto na raia dele");
        return Task.CompletedTask;
    }

    public static Task TailsOnlyWithTools()
    {
        // REGRA PRIMORDIAL: cauda de tools e para pedido que DECLARA tools.
        // Sem tools o modelo recebe JSON de tool como texto e trunca a resposta.
        const string beh = "BEHAVIOR-X";
        const string tools = "TOOLS-X";
        string withTools = global::Rochas.OpenCodeBridge.Core.HarnessTails
            .Compose("S", true, beh, tools, hasTools: true);
        TestContext.Check(withTools.Contains("TOOLS-X"), "com tools recebe a cauda de tools");
        TestContext.Check(withTools.Contains("BEHAVIOR-X"), "com tools recebe o delta");

        string without = global::Rochas.OpenCodeBridge.Core.HarnessTails
            .Compose("S", true, beh, tools, hasTools: false);
        TestContext.Check(!without.Contains("TOOLS-X"), "sem tools NAO recebe a cauda de tools");
        TestContext.Check(without.Contains("BEHAVIOR-X"), "override de behavior preservado");

        string dflt = global::Rochas.OpenCodeBridge.Core.HarnessTails.Compose("S", true,
            global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultBehavior,
            global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultTools, hasTools: false);
        TestContext.Check(dflt.Contains("PEDIDO SEM TOOLS"), "delta padrao vira variante sem tools");
        TestContext.Check(!dflt.Contains("TOOLS PRIMEIRO"), "sem 'TOOLS PRIMEIRO' no pedido sem tools");
        return Task.CompletedTask;
    }

    public static Task PreservesDynamicBlocks()
    {
        string client = "Today's date: Sat Oct 24 2026\n<env>\n  Working directory: /tmp/x\n</env>\n<available_skills>\n  <skill><id>opencode</id></skill>\n</available_skills>";
        string built = global::Rochas.OpenCodeBridge.Core.MainHarness.BuildSystem(client);
        TestContext.Check(built.Contains("Working directory: /tmp/x"), "preserva env");
        TestContext.Check(built.Contains("<available_skills>"), "preserva skills");
        TestContext.Check(built.Contains("Today's date:"), "preserva data");
        TestContext.Check(built.Contains("INSTRUCOES DO CLIENTE"), "preserva system pequeno");
        return Task.CompletedTask;
    }

    public static Task DropsMasterButKeepsEnv()
    {
        // Env/skills/data do cliente sao preservados sempre. A guarda e de
        // TAMANHO (sem heuristica de conteudo): master curto entra rotulado,
        // master grande nao e copiado para nao inflar o system.
        string master = "# Code Mode\nUse the execute tool.\n<env>\n  Working directory: /tmp/x\n</env>\n<available_skills>\n  <skill><id>opencode</id></skill>\n</available_skills>";
        string built = global::Rochas.OpenCodeBridge.Core.MainHarness.BuildSystem(master);
        TestContext.Check(built.StartsWith(Text), "base do harness no inicio");
        TestContext.Check(built.Contains("Working directory: /tmp/x"), "preserva env do master");
        TestContext.Check(built.Contains("<available_skills>"), "preserva skills do master");
        TestContext.Check(built.Contains("INSTRUCOES DO CLIENTE"), "master curto vira instrucao rotulada");

        string huge = "# Code Mode\n" + new string('x', 7000);
        string builtHuge = global::Rochas.OpenCodeBridge.Core.MainHarness.BuildSystem(huge);
        TestContext.Check(!builtHuge.Contains("INSTRUCOES DO CLIENTE"), "master grande nao e copiado");
        return Task.CompletedTask;
    }
}
