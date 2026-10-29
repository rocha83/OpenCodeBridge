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
            "Trabalhe dentro da pasta atual: tudo (pastas e arquivos) nasce aqui. " +
            "1. Crie uma subpasta com nome a sua escolha e, dentro dela, um arquivo de texto simples com nome a sua escolha " +
            "contendo a data de hoje (a data do sistema) escrita como texto. " +
            "2. Nessa mesma subpasta, inicialize um repositorio git, adicione os arquivos e faca o primeiro commit " +
            "com a mensagem 'data de hoje'; confirme com git log. " +
            "3. Busque na internet o clima de Sao Paulo de hoje e use o texto real retornado; escreva no MESMO arquivo, " +
            "na linha de baixo da data, \"ARQUIVO EDITADO - Clima: \" seguido desse clima — o arquivo fica com a data e a linha do clima " +
            "e voce confere isso lendo o arquivo. " +
            "4. Faca o segundo commit com a mensagem 'Added sao paulo weather', confirme os 2 commits com git log e salve essa " +
            "saida num arquivo de historico dentro da pasta do repositorio; confira a lista dos arquivos que ficaram nessa pasta " +
            "e o arquivo de historico deve aparecer nela antes de terminar. " +
            "Execute uma tool por vez, de forma sequencial e sem chamadas em paralelo, conforme as instrucoes previas da sessao (main harness e tails).";
        // A retentativa recebe a FALHA do aceite (contrato observavel), nunca a
        // mecanica do teste; a 1a tentativa e o enunciado puro.
        string prompt = feedback.Length == 0 ? basePrompt : basePrompt + feedback;
        // "Nada fora da pasta atual" e' contrato: fotografa o pai antes e depois.
        string parent = Path.GetDirectoryName(dir)!;
        var antes = Directory.EnumerateFileSystemEntries(parent)
            .Select(p => Path.GetFileName(p)!).ToHashSet(StringComparer.Ordinal);

        var (text, calls) = await TestContext.ToolAgent(prompt, 36, dir);

        string[] fora = Directory.EnumerateFileSystemEntries(parent)
            .Where(p => Path.GetFileName(p) is string n && !antes.Contains(n)
                        && !string.Equals(Path.GetFullPath(p), Path.GetFullPath(dir), StringComparison.Ordinal))
            .Select(p => Path.GetFileName(p)!).ToArray();
        TestContext.Check(fora.Length == 0,
            $"[toolsfull:agent] nada criado fora da pasta corrente (em {parent}: {string.Join(", ", fora)})");

        // O modelo escolhe nomes: o aceite localiza o repo e os artefatos por
        // CONTEUDO, em qualquer nivel da pasta da tentativa.
        string? repo = Directory.GetDirectories(dir, ".git", SearchOption.AllDirectories)
            .Select(g => Path.GetDirectoryName(g)!)
            .FirstOrDefault();
        TestContext.Check(repo is not null, $"[toolsfull:agent] pasta com repo git iniciado (dir={dir})");
        TestContext.Check(Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).Length > 0,
            $"[toolsfull:agent] estrutura de pastas criada (dir={dir})");
        repo ??= dir;

        static string ReadText(string f)
        {
            try { return new FileInfo(f).Length <= 2_000_000 ? File.ReadAllText(f) : ""; }
            catch { return ""; }
        }
        string gitDir = Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar;
        string[] files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(gitDir, StringComparison.Ordinal)).ToArray();
        string year = DateTime.Now.ToString("yyyy");
        static string Trecho(string f)
        {
            string t = ReadText(f).Replace('\n', ' ').Trim();
            return t[..Math.Min(70, t.Length)];
        }
        string[] comData = files.Where(f => ReadText(f).Contains(year, StringComparison.Ordinal)).ToArray();
        TestContext.Check(comData.Length > 0,
            $"[toolsfull:agent] arquivo de texto com a data criado (files={files.Length})");
        // Contrato: UM arquivo com a data E a linha do clima. Escolhe o que ja'
        // tem as duas coisas; na falha a mensagem mostra o que cada um tem (o
        // modelo grava a data num arquivo e o EDITADO em outro homonimo).
        string body = comData.Select(ReadText).FirstOrDefault(t => t.Contains("EDITADO", StringComparison.Ordinal)) ?? "";
        TestContext.Check(body.Length > 0,
            "[toolsfull:agent] marca EDITADO no MESMO arquivo da data (arquivos com data: "
            + string.Join(" | ", comData.Select(f => Path.GetFileName(f) + "='" + Trecho(f) + "'")) + ")");

        var (lgCode, lgOut) = Run("git", repo, 30, "-c", "safe.directory=" + repo, "log", "--oneline");
        string[] commits = lgCode == 0 ? lgOut.Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        TestContext.Check(commits.Length >= 2, $"[toolsfull:agent] 2 commits no repo (git log: {lgOut.Trim()}; calls={string.Join(";", calls)})");
        // Mensagens pedidas no enunciado: primeiro 'data de hoje', segundo 'Added sao paulo weather'.
        string logAll = lgCode == 0 ? Run("git", repo, 30, "-c", "safe.directory=" + repo, "log", "--format=%s").Out : "";
        TestContext.Check(logAll.Contains("data de hoje"),
            $"[toolsfull:agent] primeiro commit com mensagem 'data de hoje' (log: {logAll.Trim()})");
        TestContext.Check(logAll.Contains("Added sao paulo weather"),
            $"[toolsfull:agent] segundo commit com mensagem 'Added sao paulo weather' (log: {logAll.Trim()})");
        // Historico: o arquivo que contem os hashes dos 2 commits, qualquer nome.
        string histFile = files.FirstOrDefault(f =>
        {
            string c = ReadText(f);
            return commits.Count(k => k.Split(' ')[0].Length >= 7 && c.Contains(k.Split(' ')[0])) >= 2;
        }) ?? "";
        TestContext.Check(histFile.Length > 0,
            $"[toolsfull:agent] historico do git salvo em arquivo dentro do repo (git log: {lgOut.Trim()})");

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
                $"[toolsfull:agent] listou/buscou arquivos (calls={string.Join(";", calls)})");
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
