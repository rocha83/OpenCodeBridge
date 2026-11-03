using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Teste de tools COMPLETO: read, write, edit, glob, grep, git (init/add/commit/log)
// e busca web. Dois enunciados SEQUENCIAIS sobre o MESMO repositorio, cada um com
// versionamento proprio:
//   1) cria e versiona  (data.txt com a data + commit 'data de hoje')
//   2) ajusta e versiona (linha do clima no MESMO arquivo + commit
//      'Added sao paulo weather' + historico_git.log com os dois commits)
// Enunciado curto de dev humano: objetivo + contrato observavel, com o NOME do
// arquivo declarado (o aceite confere o artefato em disco).
internal static class E2EToolsFullTests
{
    const string DataFile = "data.txt";
    const string HistoryFile = "historico_git.log";

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

    static string Trecho(string f)
    {
        string t = ReadText(f).Replace('\n', ' ').Trim();
        return t[..Math.Min(70, t.Length)];
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
    /// Passo 1 — CRIA E VERSIONA: `data.txt` na pasta de trabalho com a data de
    /// hoje e o commit 'data de hoje'. Aceite = artefato em disco + o commit.
    /// </summary>
    static async Task CreateAttempt(string dir, string feedback)
    {
        const string basePrompt =
            "Trabalhe na pasta atual. Crie o arquivo data.txt com a data de hoje (a data do sistema) escrita como " +
            "texto. Inicialize um repositorio git nesta mesma pasta, adicione o arquivo e faca o commit com a " +
            "mensagem 'data de hoje'; confirme com git log.";
        string prompt = feedback.Length == 0 ? basePrompt : basePrompt + feedback;

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

        string dataPath = Path.Combine(dir, DataFile);
        TestContext.Check(File.Exists(dataPath),
            $"[toolsfull:create] arquivo {DataFile} criado na pasta de trabalho — nome exato do enunciado " +
            $"(subpastas: {string.Join(", ", Directory.GetDirectories(dir).Select(Path.GetFileName))}; " +
            $"arquivos: {string.Join(", ", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName))})");
        string body = ReadText(dataPath);
        TestContext.Check(body.Contains(DateTime.Now.ToString("yyyy"), StringComparison.Ordinal),
            $"[toolsfull:create] {DataFile} traz a data de hoje como texto (conteudo='{Trecho(dataPath)}')");

        string repo = dir;
        TestContext.Check(Directory.Exists(Path.Combine(repo, ".git")),
            $"[toolsfull:create] repositorio git iniciado na pasta de trabalho " +
            $"(subpastas: {string.Join(", ", Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).Select(Path.GetFileName))})");

        var (commits, log) = GitLog(repo);
        TestContext.Check(commits.Length >= 1 && log.Contains("data de hoje"),
            $"[toolsfull:create] commit 'data de hoje' no repositorio (git log: {log.Trim().Replace("\n", " / ")}; calls={string.Join(";", calls)})");

        if (TestContext.Mode == "bridge")
        {
            TestContext.Check(calls.Count > 0, $"[toolsfull:create] tools executadas (calls={calls.Count})");
            bool used(string p) => calls.Any(c => c.StartsWith(p));
            TestContext.Check(used("write(") || used("shell("),
                $"[toolsfull:create] arquivo gravado por tool (calls={string.Join(";", calls)})");
            TestContext.Check(used("git(") || calls.Any(c => c.StartsWith("shell(") && c.Contains("git")),
                $"[toolsfull:create] git executado por tool (calls={string.Join(";", calls)})");
        }
        Console.WriteLine($"  [toolsfull:create] ok ({calls.Count} calls, texto={text.Length} chars)");
    }

    /// <summary>
    /// Passo 2 — AJUSTA E VERSIONA: acrescenta a linha do clima no MESMO
    /// `data.txt`, commita 'Added sao paulo weather' e grava o `git log` em
    /// `historico_git.log`. Roda depois do passo 1, no mesmo diretorio.
    /// </summary>
    static async Task AdjustAttempt(string dir, string feedback)
    {
        const string basePrompt =
            "Na pasta atual existe um repositorio git com o arquivo data.txt ja commitado com a data de hoje. " +
            "Busque na internet o clima de Sao Paulo de hoje e acrescente em data.txt, na linha de baixo da data, " +
            "a linha 'ARQUIVO EDITADO - Clima: ' seguida do clima real retornado. Adicione a mudanca e faca o commit " +
            "com a mensagem 'Added sao paulo weather'. Grave a saida de git log (os dois commits) no arquivo historico_git.log.";
        string prompt = feedback.Length == 0 ? basePrompt : basePrompt + feedback;

        var (text, calls) = await TestContext.ToolAgent(prompt, 24, dir);

        string repo = dir;
        string dataPath = Path.Combine(dir, DataFile);
        TestContext.Check(File.Exists(dataPath), $"[toolsfull:adjust] {DataFile} continua existindo");
        string body = ReadText(dataPath);
        TestContext.Check(body.Contains(DateTime.Now.ToString("yyyy"), StringComparison.Ordinal)
            && body.Contains("EDITADO", StringComparison.Ordinal)
            && body.Contains("Clima", StringComparison.OrdinalIgnoreCase),
            $"[toolsfull:adjust] {DataFile} com a data e a linha do clima no MESMO arquivo (conteudo='{Trecho(dataPath)}')");

        var (commits, log) = GitLog(repo);
        TestContext.Check(commits.Length >= 2,
            $"[toolsfull:adjust] 2 commits no repo (git log: {log.Trim().Replace("\n", " / ")}; calls={string.Join(";", calls)})");
        TestContext.Check(log.Contains("data de hoje"),
            $"[toolsfull:adjust] primeiro commit com mensagem 'data de hoje' (log: {log.Trim().Replace("\n", " / ")})");
        TestContext.Check(log.Contains("Added sao paulo weather"),
            $"[toolsfull:adjust] segundo commit com mensagem 'Added sao paulo weather' (log: {log.Trim().Replace("\n", " / ")})");

        string histPath = Path.Combine(dir, HistoryFile);
        TestContext.Check(File.Exists(histPath),
            $"[toolsfull:adjust] historico gravado em {HistoryFile} (arquivos: {string.Join(", ", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetFileName(f)))})");
        string hist = ReadText(histPath);
        int hashes = commits.Count(k => k.Split(' ')[0].Length >= 7 && hist.Contains(k.Split(' ')[0]));
        TestContext.Check(hashes >= 2,
            $"[toolsfull:adjust] {HistoryFile} traz os dois commits do log (hashes encontrados: {hashes}; conteudo='{Trecho(histPath)}')");

        if (TestContext.Mode == "bridge")
        {
            bool used(string p) => calls.Any(c => c.StartsWith(p));
            TestContext.Check(used("edit(") || used("write("),
                $"[toolsfull:adjust] arquivo ajustado por tool (calls={string.Join(";", calls)})");
            TestContext.Check(used("read(") || used("glob(") || used("grep(")
                || calls.Any(c => c.StartsWith("shell(") && (c.Contains("ls") || c.Contains("find") || c.Contains("grep") || c.Contains("cat"))),
                $"[toolsfull:adjust] arquivos conferidos por tool (calls={string.Join(";", calls)})");
            bool web = used("webfetch(") || used("fetch(") || used("websearch(")
                || calls.Any(c => c.StartsWith("shell(") && (c.Contains("curl") || c.Contains("wget") || c.Contains("http")));
            TestContext.Check(web, $"[toolsfull:adjust] busca web executada (calls={string.Join(";", calls)})");
        }
        Console.WriteLine($"  [toolsfull:adjust] ok ({calls.Count} calls, texto={text.Length} chars)");
    }

    public static async Task FullTools()
    {
        // Dois enunciados sequenciais no MESMO diretorio: o primeiro cria e
        // versiona, o segundo ajusta e versiona. O passo 1 tem pasta nova por
        // tentativa (estado limpo); o passo 2 repete no mesmo diretorio para
        // nao perder o commit do passo 1.
        string dir = await TestContext.AgentAttempts("toolsfull:create", "/tmp/opencode/toolsfull-agent", 3,
            CreateAttempt, retryHint: "com o nome de arquivo do enunciado (data.txt) na pasta de trabalho.");
        await TestContext.RetrySame("toolsfull:adjust", dir, 3,
            AdjustAttempt, retryHint: "com os nomes de arquivo do enunciado (data.txt e historico_git.log), no MESMO repositorio.");
        Console.WriteLine("[toolsfull] cenario agentico (criar+versionar, ajustar+versionar) concluido");
    }
}
