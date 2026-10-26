using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Mesmos enunciados de tools nos DOIS transportes:
//   bridge   = caminho bridge (main/tails na bridge): HTTP /v1/responses + /api/tools/execute;
//   opencode = opencode run --standalone (o cliente executa as tools).
// Cada caso valida ARTEFATO em disco (o que o modelo diz nao conta) e, quando o
// transporte devolve o historico, tambem a chamada. Cobre os defeitos medidos nas
// sessoes de 2026-10-23: identificador mutado, glob com "|", heredoc/escape
// "\n" literal, build simulado e loop de retry no mesmo alvo.
internal static class E2EDualToolTests
{
    static string Root => Path.Combine("/tmp/opencode", $"dual-{TestContext.Mode}-{Environment.ProcessId}");

    static string Fresh(string scenario)
    {
        string dir = Path.Combine(Root, scenario);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    static string ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    /// <summary>write -> read -> edit: o artefato final tem de conter o valor editado.</summary>
    public static async Task WriteReadEdit()
    {
        string dir = Fresh("write-read-edit");
        string target = Path.Combine(dir, "nota.md");
        // Enunciado de usuario programador: objetivo e aceite, sem ditar tool/sintaxe.
        string prompt =
            "Crie o arquivo nota.md com o texto 'versao 1', confira o conteudo e troque por 'versao 2' " +
            "sem reescrever o arquivo inteiro. Responda apenas WRITE-READ-EDIT-OK.";
        var (text, calls) = await TestContext.ToolAgent(prompt, 0, dir);
        string body = ReadFile(target);
        TestContext.Check(body.Contains("versao 2"),
            $"[{TestContext.Mode}] artefato editado em disco (conteudo='{body.Trim()}', calls={string.Join(";", calls)})");
        TestContext.Check(body.Contains("nota") || body.Contains("versao"), $"[{TestContext.Mode}] arquivo nao esta vazio");
        if (TestContext.Mode == "bridge")
            TestContext.Check(calls.Any(c => c.StartsWith("edit(")), $"[{TestContext.Mode}] edit chamado (calls={string.Join(";", calls)})");
        Console.WriteLine($"  [dual:{TestContext.Mode}] write-read-edit ok ({calls.Count} calls, texto={text.Length} chars)");
    }

    /// <summary>glob sem alternancia + grep: dois arquivos, um casando o padrao.</summary>
    public static async Task GlobGrep()
    {
        string dir = Fresh("glob-grep");
        File.WriteAllText(Path.Combine(dir, "alfa.cs"), "class Alfa { }");
        File.WriteAllText(Path.Combine(dir, "beta.txt"), "beta texto");
        string prompt =
            "No diretorio atual ha arquivos .cs e .txt. Descubra qual .cs declara 'class Alfa' " +
            "e responda apenas GLOB-GREP-OK com o nome do arquivo.";
        var (text, calls) = await TestContext.ToolAgent(prompt, 0, dir);
        TestContext.Check(text.Contains("alfa", StringComparison.OrdinalIgnoreCase),
            $"[{TestContext.Mode}] resposta nomeia alfa.cs (texto='{text[..Math.Min(120, text.Length)]}')");
        if (TestContext.Mode == "bridge")
        {
            TestContext.Check(calls.Any(c => c.StartsWith("glob(")), $"[{TestContext.Mode}] glob chamado");
            TestContext.Check(calls.All(c => !c.Contains("|")), $"[{TestContext.Mode}] nenhum glob com alternancia");
        }
        Console.WriteLine($"  [dual:{TestContext.Mode}] glob-grep ok ({calls.Count} calls)");
    }

    /// <summary>Ciclo git completo (o cenario exato que quebrou por identificador mutado).</summary>
    public static async Task GitCycle()
    {
        string dir = Fresh("git-cycle");
        string dados = Path.Combine(dir, "dados.txt");
        string hist = Path.Combine(dir, "historico.log");
        string prompt =
            "Crie dados.txt com a data de hoje e coloque este diretorio sob git com um commit inicial. " +
            "Altere o arquivo e faca um segundo commit. Salve o historico de commits em historico.log " +
            "e responda apenas GIT-CYCLE-OK.";
        var (text, calls) = await TestContext.ToolAgent(prompt, 16, dir);
        TestContext.Check(Directory.Exists(Path.Combine(dir, ".git")), $"[{TestContext.Mode}] .git criado em {dir}");
        TestContext.Check(File.Exists(Path.Combine(dir, "dados.txt")), $"[{TestContext.Mode}] dados.txt criado");
        string log = ReadFile(Path.Combine(dir, "historico.log"));
        // Artefato tem que ser o historico DESTE repo: casa o SHA do commit de topo
        // (lido por git log independente) em vez da palavra "commit" — que reprovava
        // "Commit inicial" (caixa) e aprovava o log de outro repositorio.
        var (lgCode, lgOut) = Run("git", dir, 30, "log", "--oneline");
        string tip = lgCode == 0
            ? ((lgOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").Split(' ')[0])
            : "";
        TestContext.Check(tip.Length >= 7 && log.Contains(tip),
            $"[{TestContext.Mode}] historico.log com o commit real ({tip}, len={log.Length})");
        if (TestContext.Mode != "bridge")
        {
            // No modo opencode o commit e feito pelo cliente: confere direto no repo.
            var (code, outp) = Run("git", dir, 30, "log", "--oneline");
            TestContext.Check(code == 0 && outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length >= 2,
                $"[{TestContext.Mode}] 2 commits no repo (git log: {outp.Trim()})");
        }
        Console.WriteLine($"  [dual:{TestContext.Mode}] git-cycle ok ({calls.Count} calls)");
    }

    /// <summary>Probe obrigatorio: alvo com nome parecido; sem ls falha, com ls acerta.</summary>
    public static async Task ProbeBeforeRetry()
    {
        string dir = Fresh("probe");
        string real = Path.Combine(dir, "relatorio-final.txt");
        File.WriteAllText(real, "conteudo-real-42");
        string prompt =
            "Neste diretorio existe um arquivo cujo nome termina em 'final.txt', mas nao sei o nome exato. " +
            "Descubra qual e, leia o conteudo e responda apenas PROBE-OK seguido do conteudo lido.";
        var (text, calls) = await TestContext.ToolAgent(prompt, 8, dir);
        TestContext.Check(text.Contains("conteudo-real-42"),
            $"[{TestContext.Mode}] achou o alvo por probe (texto='{text[..Math.Min(160, text.Length)]}')");
        if (TestContext.Mode == "bridge")
        {
            bool probed = calls.Any(c => c.StartsWith("shell(") && (c.Contains("ls") || c.Contains("find")))
                          || calls.Any(c => c.StartsWith("glob("));
            TestContext.Check(probed, $"[{TestContext.Mode}] usou ls/glob antes de ler (calls={string.Join(";", calls)})");
        }
        Console.WriteLine($"  [dual:{TestContext.Mode}] probe ok ({calls.Count} calls)");
    }

    /// <summary>Script com erro de sintaxe: o relato tem de ser o erro, nunca sucesso.</summary>
    public static async Task NoSimulation()
    {
        string dir = Fresh("no-simulation");
        string script = Path.Combine(dir, "quebrado.sh");
        File.WriteAllText(script, "#!/bin/bash\nif [ 1 -eq 1 ]; then\n  echo faltou fechar\necho depois\n");
        string prompt =
            "Rode o script quebrado.sh e me relate apenas o que aconteceu de verdade (erro ou saida); " +
            "se falhar, diga que falhou e nao invente sucesso. Responda apenas NO-SIMULATION-OK.";
        var (text, calls) = await TestContext.ToolAgent(prompt, 6, dir);
        string all = text + "\n" + string.Join("\n", calls);
        bool reportedError = all.Contains("erro de sintaxe", StringComparison.OrdinalIgnoreCase)
                             || all.Contains("syntax error", StringComparison.OrdinalIgnoreCase)
                             || all.Contains("falhou", StringComparison.OrdinalIgnoreCase)
                             || all.Contains("failed", StringComparison.OrdinalIgnoreCase)
                             || all.Contains("unexpected", StringComparison.OrdinalIgnoreCase);
        TestContext.Check(reportedError, $"[{TestContext.Mode}] relatou a falha do script (texto='{text[..Math.Min(200, text.Length)]}')");
        if (TestContext.Mode == "bridge")
            TestContext.Check(calls.Any(c => c.Contains("bash -n") || c.Contains("quebrado.sh")),
                $"[{TestContext.Mode}] validou/executou o script de verdade");
        Console.WriteLine($"  [dual:{TestContext.Mode}] no-simulation ok ({calls.Count} calls)");
    }

    /// <summary>SQLite via python3 (cobre 'sqlite3: command not found' e heredoc proibido).</summary>
    public static async Task SqliteProbe()
    {
        string dir = Fresh("sqlite");
        string db = Path.Combine(dir, "probe.db");
        string prompt =
            "Crie um banco SQLite em probe.db com a tabela itens(id INTEGER, nome TEXT), insira 'alfa' e 'beta' " +
            "e me diga a contagem. Responda apenas SQLITE-OK e a contagem.";
        var (text, calls) = await TestContext.ToolAgent(prompt, 10, dir);
        TestContext.Check(File.Exists(db), $"[{TestContext.Mode}] banco criado em {db}");
        long rows = -1;
        try
        {
            string py = "import sqlite3;print(sqlite3.connect(\"" + db + "\").execute(\"select count(*) from itens\").fetchone()[0])";
            var (code, outp) = Run("python3", dir, 60, "-c", py);
            if (code == 0 && long.TryParse(outp.Trim(), out long n)) rows = n;
        }
        catch { }
        TestContext.Check(rows == 2, $"[{TestContext.Mode}] 2 linhas na tabela itens (lido={rows})");
        TestContext.Check(text.Contains("2") || calls.Any(c => c.Contains("itens")), $"[{TestContext.Mode}] relatou a contagem");
        Console.WriteLine($"  [dual:{TestContext.Mode}] sqlite ok ({calls.Count} calls)");
    }

    /// <summary>Matriz inteira nos dois transportes (nome do caso: E-dual-*).</summary>
    public static async Task All()
    {
        await TestContext.ToolAgent("responda ok", 1); // aquecimento (carrega modelo)
        await WriteReadEdit();
        await GlobGrep();
        await GitCycle();
        await ProbeBeforeRetry();
        await NoSimulation();
        await SqliteProbe();
    }

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
}
