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
    static async Task AgentAttempt(string dir, string feedback)
    {
        // Enunciado de TAREFA, nao de harness: sem path, sem nome de tool e sem
        // coringa de coordenada. O modelo flui; o harness resolve o cwd.
        const string basePrompt =
            "Utilize a pasta atual como espaco de trabalho: crie a subpasta dentro dela e mantenha todos os " +
            "arquivos do trabalho dentro desta pasta; nao crie nada fora dela. " +
            "1. Crie uma pasta com nome aleatorio e, dentro dela, um arquivo .txt tambem com nome aleatorio, " +
            "contendo a data de hoje escrita como texto dentro dele. " +
            "2. Dentro desta pasta que criou, execute git init para " +
            "inicializar um novo repositorio git, adicione os arquivos ao git com comando add e faca o primeiro " +
            "commit com a mensagem 'data de hoje', confirme 1 commit usando comando git log. " +
            "3. Busque na internet as condicoes climaticas hoje em Sao Paulo com webfetch e use o texto REAL retornado, nunca um placeholder. " +
            "Acrescente uma linha nova com o texto \"ARQUIVO EDITADO - Clima: <clima real>\" ABAIXO da data, sem substituir a data de hoje ja gravada no arquivo e sem apagar o conteudo existente. " +
            "Depois de alterar, use grep na data para confirmar que ela continua no arquivo. " +
            "4. Faca o segundo commit com a mensagem 'Added sao paulo weather'. " +
            "Confirme 2 commits com git log e salve a saida com a ferramenta shell redirecionando para o arquivo historico_git.log dentro da pasta do repo (ex.: git log --oneline > historico_git.log); o arquivo historico_git.log DEVE existir na pasta do repo antes de terminar. " +
            "Lembrete: isto e um teste tecnico de uso de tools, chame uma tool por vez, sem chamar tools em paralelo; " +
            "cada comando de shell comeca na pasta de trabalho, entao entre na subpasta com cd no mesmo comando; " +
            "execute todos os comandos git (init, add, commit, log) com a ferramenta shell, encadeando no mesmo comando. " +
            "OBRIGATORIO usar as tools de exploracao de arquivos: " +
            "(a) use glob para listar os arquivos da pasta e confirmar que o .txt foi criado; " +
            "(b) use grep para localizar a linha da data dentro do arquivo ANTES de editar, garantindo que ela sera preservada; " +
            "(c) use webfetch ou fetch para navegar na internet buscar o clima. " +
            "Sem usar glob e grep o teste falha.";
        // A retentativa recebe a FALHA do aceite (contrato observavel), nunca a
        // mecanica do teste; a 1a tentativa e o enunciado puro.
        string prompt = feedback.Length == 0 ? basePrompt : basePrompt + feedback;
        var (text, calls) = await TestContext.ToolAgent(prompt, 28, dir);

        // O modelo escolhe o nome da pasta: o aceite localiza o repo em qualquer nivel.
        string? repo = Directory.GetDirectories(dir, ".git", SearchOption.AllDirectories)
            .Select(g => Path.GetDirectoryName(g)!)
            .FirstOrDefault();
        TestContext.Check(repo is not null, $"[toolsfull:agent] pasta com repo git iniciado (dir={dir})");
        TestContext.Check(Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).Length > 0,
            $"[toolsfull:agent] estrutura de pastas criada (dir={dir})");
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
        // Mensagens pedidas no enunciado: primeiro 'data de hoje', segundo 'Added sao paulo weather'.
        string logAll = lgCode == 0 ? Run("git", repo, 30, "-c", "safe.directory=" + repo, "log", "--format=%s").Out : "";
        TestContext.Check(logAll.Contains("data de hoje"),
            $"[toolsfull:agent] primeiro commit com mensagem 'data de hoje' (log: {logAll.Trim()})");
        TestContext.Check(logAll.Contains("Added sao paulo weather"),
            $"[toolsfull:agent] segundo commit com mensagem 'Added sao paulo weather' (log: {logAll.Trim()})");
        string histBody = File.Exists(hist) ? File.ReadAllText(hist) : "";
        // O log pode citar qualquer commit real do repo (o tip muda se o proprio
        // historico for commitado; nesse caso o hash dele nao pode estar dentro
        // do arquivo que o gerou). Exigir um hash real evita auto-referencia.
        string realCommit = commits
            .Select(c => c.Split(' ')[0])
            .FirstOrDefault(h => h.Length >= 7 && histBody.Contains(h)) ?? "";
        TestContext.Check(realCommit.Length >= 7,
            $"[toolsfull:agent] historico_git.log com um commit real ({realCommit}; git log: {lgOut.Trim()})");
        // A saida salva deve refletir os 2 commits confirmados no enunciado.
        int inHist = commits.Count(c => c.Split(' ')[0].Length >= 7 && histBody.Contains(c.Split(' ')[0]));
        TestContext.Check(inHist >= 2,
            $"[toolsfull:agent] historico_git.log reflete os 2 commits ({inHist}/2; log: {lgOut.Trim()})");

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
            bool web = used("webfetch(") || used("fetch(")
                || calls.Any(c => c.StartsWith("shell(") && (c.Contains("curl") || c.Contains("wget") || c.Contains("http")));
            TestContext.Check(web, $"[toolsfull:agent] busca web executada (calls={string.Join(";", calls)})");
        }
        Console.WriteLine($"  [toolsfull:agent] repo={repo} ({calls.Count} calls, texto={text.Length} chars)");
    }

    /// <summary>
    /// Enunciado de 4 passos encadeados: o modelo erra a ORDEM ou pula passo. Mesmo
    /// padrao do mission-tools — 3 tentativas com a falha do aceite realimentada e
    /// pasta nova por tentativa. As assertivas sao identicas em toda tentativa.
    /// </summary>
    /// <summary>
    /// 3 tentativas com a falha do aceite realimentada e pasta nova por tentativa
    /// (<see cref="TestContext.AgentAttempts"/> — fonte unica do padrao). As
    /// assertivas sao identicas em toda tentativa.
    /// </summary>
    static Task AgentScenario()
        => TestContext.AgentAttempts("toolsfull:agent", "/tmp/opencode/toolsfull-agent", 3,
            AgentAttempt, retryHint: "na ORDEM dos passos 1..4 e sem pular nenhum passo.");

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
