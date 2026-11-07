using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Teste de tools COMPLETO: read, write, edit, glob, grep, git (init/add/commit/log)
// e compilacao. Dois enunciados SEQUENCIAIS sobre o MESMO repositorio, cada um com
// versionamento proprio:
//   1) cria e versiona  (app console C# exibindo Hello World + commit
//      'hello world console')
//   2) ajusta e versiona (troca a mensagem para Hello World v2, recompila e
//      commita 'hello world v2')
// Enunciado curto de dev humano, em linguagem natural (sem receita de codigo):
// objetivo + contrato observavel. O aceite compila e EXECUTA o app de verdade.
internal static class E2EToolsFullTests
{
    const string MsgV1 = "Hello";
    const string MsgV2 = "Hello World v2";

    static (int Code, string Out) Run(string exe, string cwd, int secs, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new Exception("sem processo: " + exe);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout " + exe); }
        return (p.ExitCode, so.Result + se.Result);
    }

    static string ReadText(string f)
    {
        try { return new FileInfo(f).Length <= 2_000_000 ? File.ReadAllText(f) : ""; }
        catch { return ""; }
    }

    static string TrechoTexto(string t)
    {
        string s = (t ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s[..Math.Min(90, s.Length)];
    }

    /// <summary>Primeiro .csproj fora de bin/obj (o app do enunciado).</summary>
    static string? FindCsproj(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .OrderBy(p => p.Length)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Fontes .cs fora de bin/obj (conteudo somado para o aceite).</summary>
    static string[] CsFiles(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                         && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Executa o app do enunciado: com .csproj usa `dotnet run --project`; sem
    /// projeto usa o app de arquivo unico do .NET 10 (`dotnet run arquivo.cs`).
    /// As duas formas sao "console C# compilado e executado pelo dotnet".
    /// </summary>
    static (int Code, string Out) RunApp(string dir)
    {
        string? proj = FindCsproj(dir);
        if (proj is not null)
        {
            var r = Run("dotnet", dir, 240, "run", "--project", proj, "-v", "q");
            if (r.Code == 0) return r;
            // Projeto quebrado (medido: csproj sem ImplicitUsings -> CS0103 no
            // Console) nao reprova de imediato: o contrato e' "app console C#
            // compila e executa", e o app de arquivo unico do .NET 10 tambem e'
            // app console. Tenta cada .cs antes de devolver o erro do projeto.
            foreach (string cs in CsFiles(dir).OrderBy(p => p.Length))
            {
                var f = Run("dotnet", dir, 240, "run", Path.GetRelativePath(dir, cs));
                if (f.Code == 0) return f;
            }
            return r;
        }
        string? single = CsFiles(dir).OrderBy(p => p.Length).FirstOrDefault();
        if (single is not null) return Run("dotnet", dir, 240, "run", Path.GetRelativePath(dir, single));
        return (-1, "sem .csproj e sem .cs na pasta");
    }

    static (string[] Commits, string Log) GitLog(string repo)
    {
        string[] args = { "-c", "safe.directory=" + repo, "log", "--oneline" };
        var (code, outp) = Run("git", repo, 30, args);
        string log = code == 0 ? Run("git", repo, 30, "-c", "safe.directory=" + repo, "log", "--format=%s").Out : "";
        var commits = code == 0 ? outp.Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        return (commits, log);
    }

    /// <summary>
    /// Passo 1 — COMPILA, EXECUTA E VERSIONA: projeto console C# ja entregue
    /// pelo teste (scaffold `dotnet new console`), executado com dotnet e
    /// versionado no commit 'hello world console'. Aceite = app executando + commit.
    /// </summary>
    static async Task CreateAttempt(string dir, string feedback)
    {
        const string basePrompt =
            "Trabalhe na pasta atual, que ja tem um projeto console em C#. Compile e execute com o dotnet e " +
            "confirme a mensagem exibida. Inicialize um repositorio git nesta mesma pasta, adicione os arquivos e " +
            "faca o commit com a mensagem 'hello world console'; confirme com git log. Nao encerre antes do commit. " +
            "Aja com ferramentas: uma tool call por vez, sem narrar plano, sem repetir o enunciado e sem descrever " +
            "o que ja fez.";
        string prompt = feedback.Length == 0 ? basePrompt : basePrompt + feedback;

        // Scaffold do projeto: o E4B nao monta csproj valido de cabeca (medido:
        // CS0103 'Console' por falta de ImplicitUsings). O teste entrega o repo
        // como um dev encontraria; o modelo faz o loop que interessa — rodar,
        // versionar, editar, recompilar, versionar.
        if (FindCsproj(dir) is null && CsFiles(dir).Length == 0)
            Run("dotnet", dir, 180, "new", "console", "--force", "-o", ".");

        string parent = Path.GetDirectoryName(dir)!;
        var antes = Directory.EnumerateFileSystemEntries(parent)
            .Select(p => Path.GetFileName(p)!).ToHashSet(StringComparer.Ordinal);

        var (text, calls) = await TestContext.ToolAgent(prompt, 24, dir);

        string[] fora = Directory.EnumerateFileSystemEntries(parent)
            .Where(p => Path.GetFileName(p) is string n && !antes.Contains(n)
                        && !string.Equals(Path.GetFullPath(p), Path.GetFullPath(dir), StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p)!).ToArray();
        TestContext.Check(fora.Length == 0,
            $"[toolsfull:create] nada criado fora da pasta corrente (em {parent}: {string.Join(", ", fora)})");

        string? proj = FindCsproj(dir);
        string[] fontes = CsFiles(dir);
        TestContext.Check(proj is not null || fontes.Length > 0,
            $"[toolsfull:create] app C# na pasta de trabalho (projeto .csproj ou arquivo .cs) " +
            $"(arquivos: {string.Join(", ", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(p => Path.GetFileName(p)).Take(12))})");
        string fonte = string.Join("\n", fontes.Select(ReadText));
        TestContext.Check(fonte.Contains(MsgV1, StringComparison.Ordinal),
            $"[toolsfull:create] fonte exibe '{MsgV1}' (arquivos: {string.Join(", ", CsFiles(dir).Select(p => Path.GetFileName(p)))})");

        string repo = dir;
        TestContext.Check(Directory.Exists(Path.Combine(repo, ".git")),
            $"[toolsfull:create] repositorio git iniciado na pasta de trabalho " +
            $"(subpastas: {string.Join(", ", Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName))})");

        var (commits, log) = GitLog(repo);
        TestContext.Check(commits.Length >= 1 && log.Contains("hello world console", StringComparison.OrdinalIgnoreCase),
            $"[toolsfull:create] commit 'hello world console' no repositorio (git log: {log.Trim().Replace("\n", " / ")}; calls={string.Join(";", calls)})");

        {
            var (code, outp) = RunApp(dir);
            TestContext.Check(code == 0 && outp.Contains(MsgV1, StringComparison.Ordinal),
                $"[toolsfull:create] app compila e exibe '{MsgV1}' (rc={code}, saida='{TrechoTexto(outp)}')");
        }

        if (TestContext.Mode == "bridge")
        {
            TestContext.Check(calls.Count > 0, $"[toolsfull:create] tools executadas (calls={calls.Count})");
            bool used(string p) => calls.Any(c => c.StartsWith(p));
            TestContext.Check(used("write(") || used("edit(") || used("shell("),
                $"[toolsfull:create] fonte gravada por tool (calls={string.Join(";", calls)})");
            TestContext.Check(calls.Any(c => c.StartsWith("shell(") && c.Contains("dotnet")),
                $"[toolsfull:create] compilacao/execucao com dotnet por tool (calls={string.Join(";", calls)})");
            TestContext.Check(used("git(") || calls.Any(c => c.StartsWith("shell(") && c.Contains("git")),
                $"[toolsfull:create] git executado por tool (calls={string.Join(";", calls)})");
        }
        Console.WriteLine($"  [toolsfull:create] ok ({calls.Count} calls, texto={text.Length} chars)");
    }

    /// <summary>
    /// Passo 2 — AJUSTA, RECOMPILA E VERSIONA: troca a mensagem exibida no MESMO
    /// app de Hello World para Hello World v2, recompila/executa e commita
    /// 'hello world v2'. Roda depois do passo 1, no mesmo diretorio.
    /// </summary>
    static async Task AdjustAttempt(string dir, string feedback)
    {
        const string basePrompt =
            "Na pasta atual existe um app console em C# que exibe Hello World, ja compilado e commitado. " +
            "Leia o codigo e troque a mensagem exibida para Hello World v2, mantendo o resto do programa. " +
            "Recompile e execute de novo, confirmando a nova mensagem na saida. Adicione a mudanca e faca o commit " +
            "com a mensagem 'hello world v2'; confirme com git log. Aja com ferramentas: uma tool call por vez, " +
            "sem narrar plano, sem repetir o enunciado e sem descrever o que ja fez.";
        string prompt = feedback.Length == 0 ? basePrompt : basePrompt + feedback;

        var (text, calls) = await TestContext.ToolAgent(prompt, 24, dir);

        string[] fontes = CsFiles(dir);
        TestContext.Check(FindCsproj(dir) is not null || fontes.Length > 0,
            $"[toolsfull:adjust] app C# continua na pasta de trabalho (projeto .csproj ou arquivo .cs)");
        string fonte = string.Join("\n", fontes.Select(ReadText));
        TestContext.Check(fonte.Contains(MsgV2, StringComparison.Ordinal),
            $"[toolsfull:adjust] fonte passa a exibir '{MsgV2}' (conteudo='{TrechoTexto(fonte)}')");

        string repo = dir;
        var (commits, log) = GitLog(repo);
        TestContext.Check(commits.Length >= 2,
            $"[toolsfull:adjust] 2 commits no repo (git log: {log.Trim().Replace("\n", " / ")}; calls={string.Join(";", calls)})");
        TestContext.Check(log.Contains("hello world console", StringComparison.OrdinalIgnoreCase),
            $"[toolsfull:adjust] primeiro commit com mensagem 'hello world console' (log: {log.Trim().Replace("\n", " / ")})");
        TestContext.Check(log.Contains("hello world v2", StringComparison.OrdinalIgnoreCase),
            $"[toolsfull:adjust] segundo commit com mensagem 'hello world v2' (log: {log.Trim().Replace("\n", " / ")})");

        {
            var (code, outp) = RunApp(dir);
            TestContext.Check(code == 0 && outp.Contains(MsgV2, StringComparison.Ordinal),
                $"[toolsfull:adjust] app recompila e exibe '{MsgV2}' (rc={code}, saida='{TrechoTexto(outp)}')");
        }

        if (TestContext.Mode == "bridge")
        {
            bool used(string p) => calls.Any(c => c.StartsWith(p));
            TestContext.Check(used("edit(") || used("write("),
                $"[toolsfull:adjust] arquivo ajustado por tool (calls={string.Join(";", calls)})");
            TestContext.Check(used("read(") || used("glob(") || used("grep(")
                || calls.Any(c => c.StartsWith("shell(") && (c.Contains("cat") || c.Contains("grep") || c.Contains("ls"))),
                $"[toolsfull:adjust] codigo conferido por tool (calls={string.Join(";", calls)})");
            TestContext.Check(calls.Any(c => c.StartsWith("shell(") && c.Contains("dotnet")),
                $"[toolsfull:adjust] recompilacao com dotnet por tool (calls={string.Join(";", calls)})");
            TestContext.Check(used("git(") || calls.Any(c => c.StartsWith("shell(") && c.Contains("git")),
                $"[toolsfull:adjust] git executado por tool (calls={string.Join(";", calls)})");
        }
        Console.WriteLine($"  [toolsfull:adjust] ok ({calls.Count} calls, texto={text.Length} chars)");
    }

    public static async Task FullTools()
    {
        // Dois enunciados sequenciais no MESMO diretorio: o primeiro cria,
        // compila e versiona; o segundo ajusta, recompila e versiona. O passo 1
        // tem pasta nova por tentativa (estado limpo); o passo 2 repete no mesmo
        // diretorio para nao perder o commit do passo 1.
        string dir = await TestContext.AgentAttempts("toolsfull:create", "/tmp/opencode/toolsfull-agent", 3,
            CreateAttempt, retryHint: "na pasta de trabalho, com o projeto console C# criado pelo proprio dotnet (compilando e executando).");
        await TestContext.RetrySame("toolsfull:adjust", dir, 3,
            AdjustAttempt, retryHint: "no MESMO repositorio e no MESMO app (troque a mensagem e recompile, sem apagar o programa).");
        Console.WriteLine("[toolsfull] cenario agentico (criar+compilar+versionar, ajustar+recompilar+versionar) concluido");
    }
}
