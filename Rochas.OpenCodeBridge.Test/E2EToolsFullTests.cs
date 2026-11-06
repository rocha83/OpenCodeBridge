using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Test;

// Teste de capacidades em DUAS FASES, como um chat humano:
//   FASE 1 (bate-papo, SEM tools): 8 perguntas em prosa sobre o que o modelo
//   sabe fazer (git, add, commit com mensagem, subpasta, internet/clima, hora
//   Linux+Windows, console .NET, publish+Docker). A resposta e' texto puro e
//   cada item vira uma linha da matriz de CONHECIMENTO (nao e' gate: mede
//   repertorio, nao execucao).
//   FASE 2 (execucao sequencial, COM tools): na MESMA pasta, o humano manda
//   fazer o que o modelo disse que sabia, em ordem, misturando contexto com a
//   ordem explicita das tools. Cada capacidade tem aceite de artefato proprio
//   e a matriz de EXECUCAO e' o veredito do step.
// Sem cutucada (TCTX_NUDGES=0), sem tails/pontes extras: so' main harness +
// tools/help. O cliente forca a call (tool_choice=required) nas rodadas.
internal static class E2EToolsFullTests
{
    const string QaPrompt =
        "Antes de mexer em qualquer coisa, me responde em portugues, item por item, SEM usar ferramentas — so' texto:\n" +
        "1) Voce sabe usar git? Se sim, quais comandos usa, na ordem?\n" +
        "2) Como se adiciona arquivos ao controle de versao?\n" +
        "3) Como se faz um commit ja' com a mensagem?\n" +
        "4) Como voce cria uma subpasta a partir da pasta atual?\n" +
        "5) Como voce acessaria a internet para buscar dados de meteorologia?\n" +
        "6) Como verificaria a hora no Linux e no Windows?\n" +
        "7) Como construiria um app console .NET C# hello world?\n" +
        "8) Como compilaria e publicaria um app .NET e faria o deploy em container Docker?";

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

    /// <summary>Resposta em prosa sem tool nenhuma (fase de conhecimento).</summary>
    static async Task<string> AskPlain(string prompt)
    {
        var body = new JsonObject
        {
            ["model"] = TestContext.SuiteModel,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "<env>\n  Working directory: /tmp/opencode\n  Is directory a git repo: no\n  Platform: linux\n</env>"
                },
                new JsonObject { ["role"] = "user", ["content"] = prompt }
            },
            ["stream"] = false
        };
        var res = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", body);
        var sb = new StringBuilder();
        if (res["output"] is JsonArray output)
            foreach (var item in output)
                if (item?["type"]?.GetValue<string>() == "message")
                    foreach (var c in item?["content"]?.AsArray() ?? new JsonArray())
                        if (c?["type"]?.GetValue<string>() == "output_text")
                            sb.Append(c?["text"]?.GetValue<string>() ?? "");
        return sb.ToString();
    }

    /// <summary>Corta a resposta nos marcadores "1)" ... "8)" para atribuir cada item.</summary>
    static string[] SplitAnswers(string text)
    {
        var marks = Regex.Matches(text, @"(?m)^\s*\**\s*([1-8])\s*[\)\.\-:]\s*");
        var parts = new string[8];
        for (int i = 0; i < marks.Count; i++)
        {
            int n = int.Parse(marks[i].Groups[1].Value);
            int start = marks[i].Index + marks[i].Length;
            int end = i + 1 < marks.Count ? marks[i + 1].Index : text.Length;
            if (n is >= 1 and <= 8 && parts[n - 1].Length == 0)
                parts[n - 1] = text[start..end];
        }
        return parts;
    }

    /// <summary>Fase 1: matriz de conhecimento, sem tools e sem gate.</summary>
    static async Task<(bool[] Known, string[] Answer)> Knowledge()
    {
        string text = await AskPlain(QaPrompt);
        TestContext.Check(text.Trim().Length > 0,
            $"[toolsfull:qa] o modelo respondeu o questionario em prosa (chars={text.Length})");
        string[] a = SplitAnswers(text);
        string All(int i) => a[i].Length > 0 ? a[i] : text;

        (string Name, Func<bool> Ok)[] caps =
        {
            ("git",      () => Regex.IsMatch(All(0), @"git\s+init") && Regex.IsMatch(All(0), @"git\s+add") && Regex.IsMatch(All(0), @"git\s+commit")),
            ("add",      () => Regex.IsMatch(All(1), @"git\s+add")),
            ("commit-m", () => Regex.IsMatch(All(2), @"git\s+commit\s+[^\n]*-[a-z]*m")),
            ("subpasta", () => Regex.IsMatch(All(3), @"mkdir")),
            ("internet", () => Regex.IsMatch(All(4), @"(curl|wget|fetch|webfetch|https?://|api)")),
            ("hora",     () => Regex.IsMatch(All(5), @"\bdate\b") && Regex.IsMatch(All(5), @"(Get-Date|time\s*/t|%time%|powershell|windows)")),
            ("console",  () => Regex.IsMatch(All(6), @"dotnet\s+new\s+console")),
            ("publish",  () => Regex.IsMatch(All(7), @"dotnet\s+publish") && Regex.IsMatch(All(7), @"docker\s+build"))
        };
        var known = new bool[caps.Length];
        var line = new StringBuilder();
        for (int i = 0; i < caps.Length; i++)
        {
            known[i] = caps[i].Ok();
            line.Append(caps[i].Name).Append('=').Append(known[i] ? "OK" : "--").Append(' ');
        }
        Console.WriteLine($"  [toolsfull:qa] conhecimento: {line.ToString().Trim()} ({known.Count(k => k)}/8)");
        return (known, a);
    }

    /// <summary>Fase 2: uma capacidade por prompt, em ordem, no MESMO workspace.</summary>
    static async Task Execute(string dir, bool[] known)
    {
        var fails = new List<string>();
        void Soft(bool ok, string label)
        {
            Console.WriteLine($"  [toolsfull:exec] {(ok ? "PASS" : "FAIL")} {label}");
            if (!ok) fails.Add(label);
        }

        async Task Step(string label, string prompt, Action verify)
        {
            try
            {
                var (text, calls) = await TestContext.ToolAgent(prompt, 8, dir);
                Console.WriteLine($"  [toolsfull:exec] {label}: {calls.Count} calls, texto={text.Length} chars");
                verify();
            }
            catch (Exception ex)
            {
                Soft(false, $"{label} (excecao: {ex.Message})");
            }
        }

        string hoje = DateTime.Now.ToString("yyyy-MM-dd");

        // S1 — subpasta + hora do sistema (o modelo citou `date` na fase 1;
        // agora tem que usar a tool e gravar a data REAL, nao inventar).
        await Step("pasta+hora",
            "Beleza, agora vamos fazer na pratica, nesta pasta mesma (a pasta atual do trabalho). Ordem: " +
            "1) cria a subpasta `dados`; " +
            "2) descobre a data e a hora do sistema com a tool de shell (no Linux o comando e' o que voce respondeu; aqui e' Linux); " +
            $"3) grava em `dados/hora.txt` a data de hoje no formato AAAA-MM-DD e a hora HH:MM. " +
            (known[3] ? "Voce disse que sabe criar subpasta com mkdir — faz agora. " : "") +
            "Usa as tools em cada passo, nao responde de cabeca.",
            () =>
            {
                string sub = Path.Combine(dir, "dados");
                string arq = Path.Combine(sub, "hora.txt");
                Soft(Directory.Exists(sub), $"subpasta 'dados' criada (subpastas: {string.Join(", ", Directory.GetDirectories(dir).Select(Path.GetFileName))})");
                Soft(File.Exists(arq), $"dados/hora.txt criado (arquivos: {string.Join(", ", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)))})");
                string body = ReadText(arq);
                Soft(body.Contains(hoje, StringComparison.Ordinal),
                    $"dados/hora.txt traz a data REAL do sistema ({hoje}) — conteudo='{Trecho(arq)}'");
            });

        // S2 — git: init/add/commit com mensagem (a pergunta 3 do questionario).
        await Step("git",
            "Agora versiona o que voce fez. Ordem: " +
            "1) `git init` nesta pasta; " +
            "2) se o git reclamar de identidade, configura `user.name` e `user.email` locais antes de seguir; " +
            "3) `git add .`; " +
            "4) commit com a mensagem exatamente `data de hoje`; " +
            "5) confirma com o log. " +
            (known[2] ? "Voce disse que sabe fazer commit ja' com a mensagem — usa o mesmo comando agora. " : "") +
            "Um passo por vez, com a tool.",
            () =>
            {
                Soft(Directory.Exists(Path.Combine(dir, ".git")), "repositorio git iniciado na pasta atual");
                var (commits, log) = GitLog(dir);
                Soft(commits.Length >= 1 && log.Contains("data de hoje", StringComparison.Ordinal),
                    $"commit com a mensagem 'data de hoje' (git log: {log.Trim().Replace("\n", " / ")})");
            });

        // S3 — internet: clima de Sao Paulo + segundo commit.
        await Step("internet+commit",
            "Boa. Agora busca na internet o clima atual de Sao Paulo e versiona: " +
            "1) usa a tool de fetch/webfetch (ou curl, como voce respondeu) na API publica " +
            "https://api.open-meteo.com/v1/forecast?latitude=-23.55&longitude=-46.63&current=temperature_2m ; " +
            "2) pega a temperatura atual e grava em `dados/clima.txt`; " +
            "3) `git add .`; 4) commit com a mensagem exatamente `Added sao paulo weather`; 5) confirma com o log.",
            () =>
            {
                string arq = Path.Combine(dir, "dados", "clima.txt");
                string body = ReadText(arq);
                Soft(File.Exists(arq), "dados/clima.txt criado");
                Soft(Regex.IsMatch(body, @"-?\d+([.,]\d+)?\s*(°|C\b|graus)", RegexOptions.IgnoreCase),
                    $"dados/clima.txt traz a temperatura retornada pela API (conteudo='{Trecho(arq)}')");
                var (commits, log) = GitLog(dir);
                Soft(commits.Length >= 2 && log.Contains("Added sao paulo weather", StringComparison.Ordinal),
                    $"segundo commit 'Added sao paulo weather' (git log: {log.Trim().Replace("\n", " / ")})");
            });

        // S4 — .NET console hello world + publish + Dockerfile.
        await Step("dotnet",
            "Por fim, o .NET: " +
            "1) cria um app console C# chamado `hello` com o comando que voce respondeu (`dotnet new console -o hello`); " +
            "2) deixa o `Program.cs` imprimindo `Hello World`; " +
            "3) roda com `dotnet run --project hello`; " +
            "4) publica com `dotnet publish hello -c Release -o publish`; " +
            "5) cria um `Dockerfile` dentro de `hello` com `FROM mcr.microsoft.com/dotnet/runtime` copiando a publicacao. " +
            "Usa as tools, um passo por vez.",
            () =>
            {
                string proj = Path.Combine(dir, "hello");
                string csproj = Path.Combine(proj, "hello.csproj");
                Soft(File.Exists(csproj), $"hello/hello.csproj criado (arquivos: {string.Join(", ", Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)).Take(12))})");
                string prog = ReadText(Path.Combine(proj, "Program.cs"));
                Soft(prog.Contains("Hello", StringComparison.Ordinal),
                    $"hello/Program.cs imprime Hello (conteudo='{(prog.Replace('\n', ' ').Trim() is var p && p.Length > 70 ? p[..70] : p)}')");
                if (File.Exists(csproj))
                {
                    var (code, outp) = Run("dotnet", dir, 180, "run", "--project", "hello");
                    Soft(code == 0 && outp.Contains("Hello", StringComparison.Ordinal),
                        $"dotnet run --project hello imprime Hello (rc={code}, out='{(outp.Replace('\n', ' ').Trim().Length > 90 ? outp.Replace('\n', ' ').Trim()[..90] : outp.Replace('\n', ' ').Trim())}')");
                }
                string pub = Path.Combine(dir, "publish");
                Soft(Directory.Exists(pub) && Directory.GetFiles(pub, "hello*.dll").Length > 0,
                    $"dotnet publish gerou a publicacao em publish/ (arquivos: {string.Join(", ", Directory.Exists(pub) ? Directory.GetFiles(pub).Select(Path.GetFileName).Take(6) : Array.Empty<string>())})");
                string docker = ReadText(Path.Combine(proj, "Dockerfile"));
                Soft(docker.Contains("FROM", StringComparison.OrdinalIgnoreCase) && docker.Contains("dotnet", StringComparison.OrdinalIgnoreCase),
                    $"hello/Dockerfile com FROM dotnet (conteudo='{docker.Replace('\n', ' ').Trim().Length switch { > 70 => docker.Replace('\n', ' ').Trim()[..70], var n => docker.Replace('\n', ' ').Trim()[..n] }}')");
            });

        TestContext.Check(fails.Count == 0,
            $"[toolsfull] capacidades executadas com tools: {string.Join(" | ", fails)}");
    }

    public static async Task FullTools()
    {
        // Fase 1 primeiro: o questionario em prosa (sem tools) mede repertorio.
        // A execucao (fase 2) roda no MESMO workspace, em ordem, como um chat
        // humano que pergunta e depois manda fazer.
        bool[] known = new bool[8];
        if (TestContext.Mode == "bridge")
            (known, _) = await Knowledge();
        else
            Console.WriteLine("  [toolsfull:qa] SKIP (questionario roda no modo bridge)");

        string dir = Path.Combine("/tmp/opencode/toolsfull-agent", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        await Execute(dir, known);
        Console.WriteLine($"[toolsfull] cenario em duas fases concluido em {dir}");
    }
}
