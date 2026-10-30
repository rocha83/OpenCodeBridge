using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core.Tools;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Pins das duas travas de tool medidas na missão de algoritmo em C# de
/// 2026-10-30: `--project` com caminho que não existe e conteúdo elidido.
/// </summary>
public static class UnitToolGuardTests
{
    /// <summary>
    /// `dotnet run --project <caminho inexistente>` devolve o caminho REAL do
    /// projeto (mesma regra das tools de arquivo). Antes o console respondia só
    /// "The provided file path does not exist" e o modelo repetia a chamada.
    /// </summary>
    public static Task ProjectPathHint()
    {
        string ws = Path.Combine(Path.GetTempPath(), "tool-proj-hint", Guid.NewGuid().ToString("N"));
        string proj = Path.Combine(ws, "mcs");
        Directory.CreateDirectory(proj);
        try
        {
            File.WriteAllText(Path.Combine(proj, "mcs.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
            var ctx = new ToolContext { WorkspaceRoot = ws, ExtraRoots = Array.Empty<string>() };
            var shell = new ShellToolHandler();

            var errado = shell.Handle("{\"command\":\"dotnet run --project " + ws + "/mcs.csproj\"}", ctx);
            TestContext.Check(!errado.Success, "projeto inexistente nao pode rodar");
            TestContext.Check(errado.Error.Contains(Path.Combine("mcs", "mcs.csproj"), StringComparison.Ordinal),
                $"erro deveria apontar o projeto real (saiu: '{errado.Error}')");

            // Caminho que existe segue o fluxo normal (a trava nao inventa recusa).
            var certo = shell.Handle("{\"command\":\"dotnet --info --project " + ws + "/mcs/mcs.csproj\"}", ctx);
            TestContext.Check(!certo.Error.Contains("projeto não encontrado", StringComparison.Ordinal),
                $"caminho existente nao pode ser recusado (saiu: '{certo.Error}')");
            return Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch { }
        }
    }

    /// <summary>
    /// `write`/`edit` recusam conteúdo com marcador de continuação no lugar do
    /// corpo (o arquivo em disco ficaria com o marcador) e aceitam o texto inteiro.
    /// </summary>
    public static Task ElisionRefused()
    {
        string ws = Path.Combine(Path.GetTempPath(), "tool-elision", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        try
        {
            var ctx = new ToolContext { WorkspaceRoot = ws, ExtraRoots = Array.Empty<string>() };
            var write = new WriteToolHandler();
            var edit = new EditToolHandler();

            string elidido = "// resolva aqui\nclass A\n{\n    // ... (resto do codigo)\n}";
            var w = write.Handle(new JsonObject
            {
                ["path"] = "A.cs",
                ["content"] = elidido
            }.ToJsonString(), ctx);
            TestContext.Check(!w.Success, "write com trecho elidido deveria ser recusado");
            TestContext.Check(!File.Exists(Path.Combine(ws, "A.cs")),
                "write recusado nao pode gravar o marcador no disco");

            var e = edit.Handle(new JsonObject
            {
                ["path"] = "A.cs",
                ["oldString"] = "x",
                ["newString"] = elidido
            }.ToJsonString(), ctx);
            TestContext.Check(!e.Success && e.Error.Contains("elidido", StringComparison.Ordinal),
                $"edit com trecho elidido deveria ser recusado (saiu: '{e.Error}')");

            // Texto completo passa e o artefato fica inteiro no disco.
            var ok = write.Handle(new JsonObject
            {
                ["path"] = "A.cs",
                ["content"] = "class A\n{\n    static void Main() { System.Console.WriteLine(\"ok\"); }\n}"
            }.ToJsonString(), ctx);
            TestContext.Check(ok.Success, $"write completo deveria passar (saiu: '{ok.Error}')");
            TestContext.Check(File.ReadAllText(Path.Combine(ws, "A.cs")).Contains("WriteLine", StringComparison.Ordinal),
                "artefato completo no disco");
            return Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch { }
        }
    }

    /// <summary>
    /// `edit` com o arquivo inteiro no `newString` e uma linha curta no `oldString`
    /// e' reescrita total disfarcada: aplica e duplica a declaracao (CS1022 medido no
    /// algo-cs-sort). Recusa com a tool certa; o edit de TRECHO continua passando.
    /// </summary>
    public static Task WholeFileRewriteRefused()
    {
        string ws = Path.Combine(Path.GetTempPath(), "tool-rewrite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        try
        {
            var ctx = new ToolContext { WorkspaceRoot = ws, ExtraRoots = Array.Empty<string>() };
            var edit = new EditToolHandler();
            File.WriteAllText(Path.Combine(ws, "A.cs"), "using System;\n\nclass A\n{\n    static void Main() { }\n}\n");
            string inteiro = "using System;\nusing System.Linq;\n\nclass A\n{\n    static void Main() { }\n}\n";
            var r = edit.Handle(new JsonObject
            {
                ["path"] = "A.cs",
                ["oldString"] = "}",
                ["newString"] = inteiro
            }.ToJsonString(), ctx);
            TestContext.Check(!r.Success && r.Error.Contains("write", StringComparison.Ordinal),
                $"edit com arquivo inteiro deveria mandar usar write (saiu: '{r.Error}')");
            TestContext.Check(File.ReadAllText(Path.Combine(ws, "A.cs")).Contains("static void Main", StringComparison.Ordinal)
                && !File.ReadAllText(Path.Combine(ws, "A.cs")).Contains("using System.Linq", StringComparison.Ordinal),
                "recusa nao altera o arquivo");
            var ok = edit.Handle(new JsonObject
            {
                ["path"] = "A.cs",
                ["oldString"] = "static void Main() { }",
                ["newString"] = "static void Main() { System.Console.WriteLine(\"ok\"); }"
            }.ToJsonString(), ctx);
            TestContext.Check(ok.Success, $"edit de trecho continua passando (saiu: '{ok.Error}')");
            return Task.CompletedTask;
        }
        finally
        {
            try { Directory.Delete(ws, true); } catch { }
        }
    }
}
