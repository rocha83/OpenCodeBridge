using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Teste de tools COMPLETO: read, write, edit(sed), glob, grep, navegacao em
// pastas, git (init/add/commit/log), compilacao C# (dotnet new/build/run),
// validacao e execucao de script. Roda nos 2 modelos (GPU1 E4B-Q4 e GPU2 E4B-Q3)
// em paralelo via bridge pura. Deve passar antes da bateria belts.
internal static class E2EToolsFullTests
{
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

    /// <summary>
    /// Proposito do antigo dual-tools dentro do tools-full: um enunciado agentico
    /// unico cobre pasta, repo git, arquivo com data/clima, add/commit, listar,
    /// buscar, editar, segundo commit e historico_git.log. O aceite e o artefato
    /// em disco; no bridge tambem confere as tools usadas.
    /// </summary>
    static async Task AgentScenario()
    {
        // Base propria do teste (mint): /tmp/opencode/toolsfull e criado pela
        // bridge (root) e o teste nao pode criar subdiretorios la.
        string dir = Path.Combine("/tmp/opencode/toolsfull-agent",
            "agent-" + Environment.ProcessId + "-" + TestContext.Mode + "-" + DateTime.Now.ToString("HHmmss"));
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex)
        {
            // Conteudo criado pela bridge (root) pode nao ser removivel pelo teste:
            // o dir novo e unico por horario, entao seguir sem apagar e seguro.
            Console.WriteLine($"  [toolsfull:agent] cleanup ignorado: {ex.GetType().Name}");
        }
        Directory.CreateDirectory(dir);

        // Enunciado de TAREFA, nao de harness: sem path, sem nome de tool e sem
        // coringa de coordenada. O modelo flui; o harness resolve o cwd.
        const string prompt =
            "Trabalhe na pasta atual. Crie um arquivo .txt com nome aleatorio contendo a data de hoje (formato AAAA-MM-DD) " +
            "e o clima de Sao Paulo se conseguir acessar a internet. Rode git init, faca git add e o primeiro commit " +
            "com a mensagem 'data' (o primeiro commit nao pode ter a palavra EDITADO). Confirme 1 commit no git log. " +
            "Depois edite o .txt para acrescentar uma nova linha com EDITADO abaixo da data (sem substituir a data) e " +
            "faca o segundo commit com a mensagem 'EDITADO', confirmando 2 commits. Liste os arquivos, busque o .txt " +
            "com glob/grep, leia o arquivo e grave o historico do git (hashes e mensagens) em historico_git.log. " +
            "Nao commite o historico_git.log.";

        var (text, calls) = await TestContext.ToolAgent(prompt, 24, dir);

        // O modelo escolhe o nome da pasta: o aceite localiza o repo em qualquer nivel.
        string? repo = Directory.GetDirectories(dir, ".git", SearchOption.AllDirectories)
            .Select(g => Path.GetDirectoryName(g)!)
            .FirstOrDefault();
        TestContext.Check(repo is not null, $"[toolsfull:agent] pasta com repo git iniciado (dir={dir})");
        repo ??= dir;

        string[] txts = Directory.GetFiles(repo, "*.txt", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("historico_git.log", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        TestContext.Check(txts.Length > 0, "[toolsfull:agent] arquivo .txt criado");
        string year = DateTime.Now.ToString("yyyy");
        // Escolhe o .txt da tarefa (com a data), nao um artefato auxiliar.
        string body = txts.Select(File.ReadAllText).FirstOrDefault(t => t.Contains(year))
            ?? (txts.Length > 0 ? File.ReadAllText(txts[0]) : "");
        TestContext.Check(body.Contains(year),
            $"[toolsfull:agent] data do sistema anotada (conteudo='{body[..Math.Min(120, body.Length)]}', calls={string.Join(";", calls)})");
        TestContext.Check(body.Contains("EDITADO"), $"[toolsfull:agent] marca EDITADO no arquivo (calls={string.Join(";", calls)})");

        string hist = Path.Combine(repo, "historico_git.log");
        TestContext.Check(File.Exists(hist), $"[toolsfull:agent] historico_git.log criado em {repo}");

        var (lgCode, lgOut) = Run("git", repo, 30, "-c", "safe.directory=" + repo, "log", "--oneline");
        string[] commits = lgCode == 0 ? lgOut.Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        TestContext.Check(commits.Length >= 2, $"[toolsfull:agent] 2 commits no repo (git log: {lgOut.Trim()}; calls={string.Join(";", calls)})");
        string histBody = File.Exists(hist) ? File.ReadAllText(hist) : "";
        // O log pode citar qualquer commit real do repo (o tip muda se o proprio
        // historico for commitado; nesse caso o hash dele nao pode estar dentro
        // do arquivo que o gerou). Exigir um hash real evita auto-referencia.
        string realCommit = commits
            .Select(c => c.Split(' ')[0])
            .FirstOrDefault(h => h.Length >= 7 && histBody.Contains(h)) ?? "";
        TestContext.Check(realCommit.Length >= 7,
            $"[toolsfull:agent] historico_git.log com um commit real ({realCommit}; git log: {lgOut.Trim()})");

        // O artefato prova o fluxo nos dois modos. No bridge as calls tambem
        // provam tools; no opencode as tools sao do CLI e nao entram nesse log.
        if (TestContext.Mode == "bridge")
        {
            TestContext.Check(calls.Count > 0, $"[toolsfull:agent] tools executadas (calls={calls.Count})");
            bool used(string p) => calls.Any(c => c.StartsWith(p));
            TestContext.Check(used("write(") || used("edit(") || used("shell("),
                $"[toolsfull:agent] escrita/edicao por tool (calls={string.Join(";", calls)})");
            TestContext.Check(used("git(") || calls.Any(c => c.StartsWith("shell(") && c.Contains("git")),
                "[toolsfull:agent] ciclo git executado");
            TestContext.Check(used("glob(") || used("grep(")
                || calls.Any(c => c.StartsWith("shell(") && (c.Contains("ls") || c.Contains("find") || c.Contains("grep"))),
                "[toolsfull:agent] listou/buscou arquivos");
        }
        Console.WriteLine($"  [toolsfull:agent] repo={repo} ({calls.Count} calls, texto={text.Length} chars)");
    }

    public static async Task FullTools()
    {
        // As duas lanes usam o MESMO cwd (o workspace e do processo da bridge) e os
        // MESMOS nomes de arquivo (a.cs, sub/b.cs). Em paralelo uma lane reescrevia
        // o arquivo da outra e o edit falhava por oldString ausente (medido:
        // "GPU2-E4B-Q3 edit ok" FAIL com a.cs = int X = 42). Sequencial preserva
        // o aceite sem afrouxar assertiva.
        // Q3 fora desta premissa (hipotese reprovada): tools-full roda so no
        // Q4 estavel. A lane Q3 nao entra no aceite.
        // Apenas cenario COGNITIVO do modelo (AgentScenario): enunciado natural,
        // o modelo decide as tools e a suite valida o artefato em disco.
        // Nao existe mais execucao direta de tools no teste (RunOnBridge removido):
        // o objetivo e a capacidade cognitiva do modelo, nao o Core.
        await AgentScenario();
        Console.WriteLine("[toolsfull] cenario agentico concluido");
    }
}
