// Belts headless no agente real (opencode run, modo implicito): sem browser, sem
// web UI e sem a bridge "pura" — cada enunciado roda no workspace do belt e o
// resultado e conferido por artefato + log.
// Uso: dotnet ... --opencode-run [--belts tools-full,tools-dev-pipe]
//      [--agent build] [--model openai/gemma4-12b-build] [--workdir /tmp/opencode/belts]
using System.Diagnostics;
using System.Text;

namespace Rochas.OpenCodeBridge.Web.Test.Integration;

internal static class OpencodeRunBeltTests
{
    public static async Task<int> RunAsync(string[] only, string agent, string model, string baseDir)
    {
        int failures = 0;
        var belts = Ui.BeltUiTests.Select(only);
        if (belts.Length == 0)
        {
            Console.WriteLine("[run] FAIL nenhuma faixa selecionada");
            return 1;
        }
        Directory.CreateDirectory(baseDir);

        foreach (var belt in belts)
        {
            string dir = Path.Combine(baseDir, $"{belt.Key}-{DateTime.Now:HHmmss}");
            Directory.CreateDirectory(dir);
            Console.WriteLine($"[run] faixa {belt.Key} em {dir}");

            var psi = new ProcessStartInfo("/usr/local/bin/opencode")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // `PWD` do processo pai engana o opencode (ele resolve o workspace por
            // essa env, nao pelo cwd real) - sem isto o agente escreve no repo do teste.
            psi.Environment["PWD"] = dir;
            foreach (var a in new[] { "run", "--standalone", "--thinking", "--auto", "--agent", agent, "--model", model, belt.Prompt })
                psi.ArgumentList.Add(a);

            var sb = new StringBuilder();
            using var p = Process.Start(psi) ?? throw new Exception("sem processo opencode");
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            bool exited = await Task.Run(() => p.WaitForExit(belt.TimeoutMin * 60 * 1000));
            if (!exited) { try { p.Kill(true); } catch { } }
            string output = sb.ToString();
            string log = Path.Combine(dir, "_opencode.log");
            try { await File.WriteAllTextAsync(log, output); } catch { }

            bool unknownTool = output.Contains("Unknown tool", StringComparison.OrdinalIgnoreCase);
            var files = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/.git/", StringComparison.Ordinal))
                .ToList();
            string detail = "";
            bool artifacts = belt.Key switch
            {
                "tools-full" => ValidateToolsFull(dir, out detail),
                "tools-dev-pipe" => ValidateDevPipe(dir, out detail),
                _ => files.Count > 1,
            };

            bool ok = exited && !unknownTool && output.Length > 0 && artifacts;
            Console.WriteLine($"[run] {(ok ? "PASS" : "FAIL")} {belt.Key}: timeout={!exited} out={output.Length} artefatos={artifacts} {detail} unknownTool={unknownTool} ({log})");
            if (!ok) failures++;
        }

        Console.WriteLine(failures == 0 ? "[run] PASS" : $"[run] FAIL={failures}");
        return failures;
    }

    /// <summary>Historico git real do repo: hash, assunto e arquivos por commit.</summary>
    /// <param name="repo">Pasta do repositorio.</param>
    /// <returns>Commits na ordem do log (vazio quando nao ha repo).</returns>
    static List<(string Hash, string Subject, List<string> Files)> GitLog(string repo)
    {
        var result = new List<(string Hash, string Subject, List<string> Files)>();
        if (repo.Length == 0 || !Directory.Exists(Path.Combine(repo, ".git"))) return result;
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "log", "--pretty=format:%h|%s", "--name-only" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return result;
            string outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20000);
            foreach (var rawLine in outp.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                int bar = line.IndexOf('|');
                if (bar > 0 && bar <= 12 && line[..bar].All(Uri.IsHexDigit))
                {
                    result.Add((line[..bar], line[(bar + 1)..], new List<string>()));
                    continue;
                }
                if (result.Count > 0) result[^1].Files.Add(line);
            }
        }
        catch { }
        return result;
    }

    /// <summary>Aceite tools-full: repo tooltest, 2+ commits e plano.md versionado na criacao e na edicao.</summary>
    /// <param name="dir">Workspace do belt.</param>
    /// <param name="detail">Resumo do historico para o log.</param>
    /// <returns>True quando o historico comprova criacao + edicao.</returns>
    static bool ValidateToolsFull(string dir, out string detail)
    {
        string repo = Path.Combine(dir, "tooltest");
        string plan = Path.Combine(repo, "docs", "plano.md");
        var hist = GitLog(repo);
        int touches = hist.Count(c => c.Files.Any(f => f.EndsWith("docs/plano.md", StringComparison.Ordinal)));
        detail = $"git={hist.Count}cmt plano_em={touches}cmt";
        return File.Exists(plan) && hist.Count >= 2 && touches >= 2;
    }

    /// <summary>Aceite tools-dev-pipe: repo da subpasta, 2+ commits, .txt versionado na criacao e na edicao, AWC e zip.</summary>
    /// <param name="dir">Workspace do belt.</param>
    /// <param name="detail">Resumo do historico para o log.</param>
    /// <returns>True quando o historico e os artefatos conferem.</returns>
    static bool ValidateDevPipe(string dir, out string detail)
    {
        string repo = Directory.EnumerateDirectories(dir, "devpipe-test-*").FirstOrDefault() ?? "";
        var hist = GitLog(repo);
        string txt = repo.Length > 0
            ? Directory.EnumerateFiles(repo, "*.txt", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? ""
            : "";
        string rel = txt.Length > 0 ? Path.GetFileName(txt) : "";
        int touches = rel.Length > 0 ? hist.Count(c => c.Files.Any(f => f.Equals(rel, StringComparison.Ordinal))) : 0;
        bool content = false;
        try { content = txt.Length > 0 && File.ReadAllText(txt).Contains("AWC"); } catch { }
        bool zip = repo.Length > 0 && Directory.EnumerateFiles(repo, "*.zip", SearchOption.TopDirectoryOnly).Any();
        detail = $"git={hist.Count}cmt txt_em={touches}cmt awc={content} zip={zip}";
        return repo.Length > 0 && hist.Count >= 2 && touches >= 2 && content && zip;
    }
}
