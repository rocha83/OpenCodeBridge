using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Unit da estrategia de tools (ToolStrategy): resgate deterministico do padrao
// Code Mode execute{ code: "return tools.shell({...})" } -> shell({...}).
// Sem HTTP e sem modelo: exercita direto o parser JS e a nota de sistema.
internal static class UnitToolStrategyTests
{
    static (string Name, string Args) Unwrap(string code)
    {
        string arguments = new JsonObject { ["code"] = code }.ToJsonString();
        return global::Rochas.OpenCodeBridge.Program.UnwrapExecuteCall("execute", arguments);
    }

    static List<(string Name, string Args)> UnwrapAll(string code)
    {
        string arguments = new JsonObject { ["code"] = code }.ToJsonString();
        return global::Rochas.OpenCodeBridge.Program.UnwrapExecuteCalls("execute", arguments);
    }

    public static Task ExecuteToShell()
    {
        var (name, args) = Unwrap("// usa shell direto\nreturn tools.shell({ command: `mkdir -p projeto_teste && echo \"ola\" > projeto_teste/notas.txt && cd projeto_teste && git init` });");
        TestContext.Check(name == "shell", "execute+tools.shell vira shell");
        TestContext.Check(
            JsonNode.Parse(args)?["command"]?.GetValue<string>()
                == "mkdir -p projeto_teste && echo \"ola\" > projeto_teste/notas.txt && cd projeto_teste && git init",
            "command preservado no unwrap");
        return Task.CompletedTask;
    }

    public static Task ExecuteToWriteSingleQuotes()
    {
        var (name, args) = Unwrap("await tools.write({ path: 'a.txt', content: 'linha 1\\nlinha 2', })");
        TestContext.Check(name == "write", "execute+tools.write vira write");
        var parsed = JsonNode.Parse(args)?.AsObject();
        TestContext.Check(parsed?["path"]?.GetValue<string>() == "a.txt", "path do write preservado");
        TestContext.Check(parsed?["content"]?.GetValue<string>() == "linha 1\nlinha 2", "escape \\n do write preservado");
        return Task.CompletedTask;
    }

    public static Task ExecuteBashMapsToShell()
    {
        var (name, _) = Unwrap("return tools.bash({ command: 'ls -la' })");
        TestContext.Check(name == "shell", "tools.bash vira shell (nome do opencode)");
        return Task.CompletedTask;
    }

    public static Task ExecuteWithFsGetsGuidance()
    {
        var calls = UnwrapAll("const fs = require('fs');\nfs.mkdirSync('x');");
        TestContext.Check(calls.Count == 1 && calls[0].Name == "shell", "require/fs vira shell guiado");
        TestContext.Check(calls.Count == 1 && calls[0].Args.Contains("NAO existe require/fs"), "mensagem guiada no resultado");
        return Task.CompletedTask;
    }

    public static Task ExecuteMultiCallScript()
    {
        var calls = UnwrapAll("const a = 1;\nawait tools.shell({ command: 'mkdir -p x' });\nawait tools.write({ path: 'x/a.txt', content: 'A' });\nawait tools.shell({ command: `cd x && git init && git commit -m 'c'` });");
        TestContext.Check(calls.Count == 3, $"script com 3 tools vira 3 chamadas (veio {calls.Count})");
        TestContext.Check(calls.Count > 0 && calls[0].Name == "shell" && calls[0].Args.Contains("mkdir -p x"), "1a chamada shell preservada");
        TestContext.Check(calls.Count > 1 && calls[1].Name == "write" && calls[1].Args.Contains("x/a.txt"), "2a chamada write preservada");
        TestContext.Check(calls.Count > 2 && calls[2].Name == "shell" && calls[2].Args.Contains("git init"), "3a chamada shell preservada");
        return Task.CompletedTask;
    }

    public static Task SanitizeKeepsValidJson()
    {
        const string ok = """{"path":"a/b.txt","oldString":"A","newString":"ABC"}""";
        var (name, args) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("edit", ok);
        TestContext.Check(name == "edit" && args == ok, "argumentos validos passam intactos");
        return Task.CompletedTask;
    }

    public static Task SanitizeStripsLeakedTokens()
    {
        const string dirty = """{"path":"a/b.txt","content":"x"}<tool_call|><|channel>thought""";
        var (name, args) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("write", dirty);
        TestContext.Check(name == "write", "tool preservada quando o JSON sobrevive ao corte");
        TestContext.Check(args.Contains("\"a/b.txt\"", StringComparison.Ordinal), "path preservado apos limpeza");
        TestContext.Check(!args.Contains("<tool_call", StringComparison.Ordinal), "token de template removido");
        return Task.CompletedTask;
    }

    public static Task SanitizeGuidesBrokenArgs()
    {
        const string broken = """{"newString": "ABC\",oldString:\"A\",path:\"a/b.txt\"}}<tool_call|>""";
        var (name, args) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("edit", broken);
        TestContext.Check(name == "shell", "argumentos irrecuperaveis viram shell guiado");
        TestContext.Check(args.Contains("eemita a chamada com JSON completo"), "guia pede reemissao com JSON completo");
        TestContext.Check(args.Contains("edit=path+oldString+newString"), "guia lembra as chaves da edit");
        return Task.CompletedTask;
    }

    public static Task SanitizeGuidesMissingRequiredKeys()
    {
        const string noPath = """{"newString":"ABC\",oldString:","replaceAll":true}""";
        var (name, args) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("edit", noPath);
        TestContext.Check(name == "shell", "edit sem path/oldString vira shell guiado");
        TestContext.Check(args.Contains("faltam as chaves", StringComparison.Ordinal)
                          && args.Contains("path", StringComparison.Ordinal)
                          && args.Contains("oldString", StringComparison.Ordinal), "guia lista as chaves faltantes");
        TestContext.Check(args.Contains("Nao aninhe JSON", StringComparison.Ordinal), "guia proibe JSON aninhado");
        return Task.CompletedTask;
    }

    public static Task SanitizeKeepsCompleteWrite()
    {
        const string ok = """{"path":"a/b.txt","content":"x"}""";
        var (name, args) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("write", ok);
        TestContext.Check(name == "write" && args == ok, "write com path+content passa intacta");
        return Task.CompletedTask;
    }

    public static Task SanitizeGuidesMissingContent()
    {
        const string noContent = """{"path":"a/b.txt"}""";
        var (name, args) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("write", noContent);
        TestContext.Check(name == "shell" && args.Contains("content", StringComparison.Ordinal), "write sem content vira shell guiado");
        return Task.CompletedTask;
    }

    public static Task SanitizeShellTrimsLeakedTemplate()
    {
        const string args = """{"command":"date +\"%Y-%m-%d_%H-%M-%S\"}<tool_call|>}<tool_call|><|channel>thought"}""";
        var (name, fixedArgs) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("shell", args);
        TestContext.Check(name == "shell", "shell continua shell");
        var cmd = global::System.Text.Json.Nodes.JsonNode.Parse(fixedArgs)!["command"]!.GetValue<string>();
        TestContext.Check(cmd == "date +\"%Y-%m-%d_%H-%M-%S\"", "comando cortado no marcador e balanceado");
        return Task.CompletedTask;
    }

    public static Task SanitizeShellPromotesInlineCall()
    {
        const string args = """{"command":"websearch{query: \"clima hoje em sao paulo\"}<tool_call|>]}"}""";
        var (name, fixedArgs) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("shell", args);
        TestContext.Check(name == "websearch", "chamada inline vira tool nativa");
        var query = global::System.Text.Json.Nodes.JsonNode.Parse(fixedArgs)!["query"]!.GetValue<string>();
        TestContext.Check(query == "clima hoje em sao paulo", "argumentos da chamada inline preservados");
        return Task.CompletedTask;
    }

    public static Task SanitizeGuidesSubagentMissingPrompt()
    {
        const string args = """{"agent":"general","description":"Planejamento do Dominio\",prompt:"}""";
        var (name, fixedArgs) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("subagent", args);
        TestContext.Check(name == "shell", "subagent sem prompt vira shell guiado");
        TestContext.Check(fixedArgs.Contains("prompt", StringComparison.Ordinal), "guia cita a chave prompt");
        return Task.CompletedTask;
    }

    public static Task SanitizeKeepsSkillWithId()
    {
        const string ok = """{"id":"opencode"}""";
        var (name, fixedArgs) = global::Rochas.OpenCodeBridge.Program.SanitizeToolCall("skill", ok);
        TestContext.Check(name == "skill" && fixedArgs == ok, "skill com id passa intacta");
        return Task.CompletedTask;
    }

    public static Task HarnessTailCarriesQualityCore()
    {
        // O nucleo de qualidade saiu das tails e mora no MainHarness; as
        // tails agora sao so o delta da bridge. O teste guarda o novo contrato.
        string core = global::Rochas.OpenCodeBridge.Core.MainHarness.Text;
        foreach (string must in new[] { "ACEITE", "explique a dificuldade", "edit", "stub", "contexto", "bash -n" })
            TestContext.Check(core.Contains(must, StringComparison.Ordinal), $"nucleo de qualidade presente: {must}");
        string formula = global::Rochas.OpenCodeBridge.Program.WithFormula("system base");
        TestContext.Check(formula.StartsWith("system base", StringComparison.Ordinal), "system original preservado");
        return Task.CompletedTask;
    }

    public static Task StripExecuteFromChatTools()
    {
        var tools = JsonNode.Parse("""[{"type":"function","function":{"name":"execute","parameters":{}}},{"type":"function","function":{"name":"shell","parameters":{}}}]""")!.AsArray();
        int removed = global::Rochas.OpenCodeBridge.Program.StripExecuteTools(tools, "chat");
        TestContext.Check(removed == 1 && tools.Count == 1, "execute sai do catalogo chat (shell fica)");
        TestContext.Check(tools[0]!["function"]!["name"]!.GetValue<string>() == "shell", "shell preservada no catalogo");
        return Task.CompletedTask;
    }

    public static Task StripExecuteFromResponsesTools()
    {
        var tools = JsonNode.Parse("""[{"type":"function","name":"execute","parameters":{}},{"type":"function","name":"write","parameters":{}}]""")!.AsArray();
        int removed = global::Rochas.OpenCodeBridge.Program.StripExecuteTools(tools, "responses");
        TestContext.Check(removed == 1 && tools.Count == 1, "execute sai do catalogo responses (write fica)");
        TestContext.Check(tools[0]!["name"]!.GetValue<string>() == "write", "write preservada no catalogo");
        return Task.CompletedTask;
    }

    public static Task NeutralizeCodeModeWritesDirectToolsNote()
    {
        var chat = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "system",
                ["content"] = "intro\n# Code Mode\nUse the `execute` tool to call the tools listed below.\n" +
                              "## Available tools\n- tools.opencode.models({...})\n" +
                              "Skills provide specialized instructions and workflows for specific tasks.\n" +
                              "# Instrucoes globais do agente\nregras"
            })
        };
        int hits = global::Rochas.OpenCodeBridge.Program.NeutralizeCodeMode(chat);
        string text = chat["messages"]![0]!["content"]!.GetValue<string>();
        TestContext.Check(hits == 1, "bloco Code Mode detectado e reescrito");
        TestContext.Check(!text.Contains("Use the `execute` tool"), "instrucao do execute removida");
        TestContext.Check(text.Contains("NAO oferece `execute`"), "nota de tools diretas injetada");
        TestContext.Check(text.Contains("Skills provide specialized"), "catalogo de skills preservado");
        TestContext.Check(text.Contains("# Instrucoes globais"), "tail (AGENTS.md) preservado");
        TestContext.Check(text.StartsWith("intro", StringComparison.Ordinal), "cabeca do system preservada");
        return Task.CompletedTask;
    }

    public static Task ExecuteMultiCallCapped()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 12; i++) sb.Append($"await tools.shell({{ command: 'echo {i}' }});\n");
        var calls = UnwrapAll(sb.ToString());
        TestContext.Check(calls.Count == 8, $"cap de 8 chamadas por execute (veio {calls.Count})");
        return Task.CompletedTask;
    }

    public static Task NonExecuteStaysIntact()
    {
        var (name, args) = global::Rochas.OpenCodeBridge.Program.UnwrapExecuteCall("shell", "{\"command\":\"ls\"}");
        TestContext.Check(name == "shell" && args.Contains("ls"), "tool que nao e execute passa intacta");
        return Task.CompletedTask;
    }

    public static Task ForeignNamespaceStaysIntact()
    {
        var (name, _) = Unwrap("return tools.opencode.models({})");
        TestContext.Check(name == "execute", "namespace fora do catalogo direto nao e resgatado");
        return Task.CompletedTask;
    }
}
