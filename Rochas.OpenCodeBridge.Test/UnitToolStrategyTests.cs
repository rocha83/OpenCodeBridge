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

    public static Task ExecuteWithFsStaysIntact()
    {
        var (name, args) = Unwrap("const fs = require('fs');\nfs.mkdirSync('x');");
        TestContext.Check(name == "execute", "codigo fs (sem tools.X) fica intacto");
        TestContext.Check(args.Contains("mkdirSync"), "argumentos intactos sem resgate");
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
