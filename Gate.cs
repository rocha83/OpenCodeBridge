// ===========================================================================
//  Gate — helper de integridade pos-tarefa (mesmo projeto, sem binario extra).
// ---------------------------------------------------------------------------
//  Nucleo fixo: roda o agente pinado -> exige commit novo -> roda o build ->
//  aplica politicas. Politica especifica mora em gate.json no repo alvo.
//  Identificadores em en-US, comentarios em pt-BR.
//
//  USO: vllm-ocode-bridge.dll gate <repo> "<prompt>" [texto ...] [--config f.json] [--review]
//  gate.json: { "build": "cmd", "requireCommit": true,
//               "forbidPaths": ["bin/","obj/"], "mustContain": ["texto"] }
// ===========================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qwen3Bridge;

internal static class Gate
{
    /// <summary>Ponto de entrada: le args + gate.json, roda agente e verificacoes.</summary>
    /// <param name="cliArgs">Repo, prompt, textos obrigatorios, --config e --review.</param>
    /// <returns>0 PASS, 1 FAIL, 2 uso invalido.</returns>
    internal static int Run(string[] cliArgs)
    {
        if (cliArgs.Length < 2)
        {
            Console.Error.WriteLine("uso: vllm-ocode-bridge.dll gate <repo> \"<prompt>\" [texto ...] [--config f.json] [--review]");
            return 2;
        }
        string repo = cliArgs[0];
        string prompt = cliArgs[1];
        var mustContain = new List<string>();
        string configName = "gate.json";
        bool review = false;
        for (int i = 2; i < cliArgs.Length; i++)
        {
            if (cliArgs[i] == "--config" && i + 1 < cliArgs.Length) { configName = cliArgs[++i]; }
            else if (cliArgs[i] == "--review") { review = true; }
            else mustContain.Add(cliArgs[i]);
        }

        // Politica do projeto (se existir); CLI soma aos mustContain do arquivo.
        var policy = LoadPolicy(Path.Combine(repo, configName));
        mustContain.AddRange(policy.MustContain);

        // Baseline = ultimo HEAD aprovado (arquivo dentro de .git, nunca commitado).
        // Sem baseline: usa o HEAD atual (nada a revisar ate novo trabalho).
        string gitDir = Run(repo, "git", "rev-parse --git-dir");
        string baselineFile = Path.Combine(repo, gitDir, "gate-baseline");
        string baseline = File.Exists(baselineFile) ? File.ReadAllText(baselineFile).Trim() : "";
        if (baseline == "") { baseline = Run(repo, "git", "rev-parse HEAD"); }

        string before = Run(repo, "git", "rev-parse HEAD");

        // Agente pinado no Qwen local (garantia do AGENTS.md). Timeout de 9min:
        // calc travou em 10min sem responder — FAIL em vez de pendurar.
        string agentOut = RunAgent(repo, prompt);
        try { File.AppendAllText(policy.LogFile, agentOut + "\n"); } catch { }

        bool fail = false;

        // 1) Ha algo novo desde a baseline? (commit local e livre; o portao
        //    segura so o "push" = marco aprovado.)
        string head = Run(repo, "git", "rev-parse HEAD");
        if (head == baseline) { Console.WriteLine("Nada a revisar (HEAD = baseline)."); return 0; }
        if (policy.RequireCommit && head == before)
        { Console.WriteLine("AVISO: agente nao commitou (permitido; revisao cobre working tree)"); }

        // 2) Caminhos proibidos no intervalo baseline..HEAD + working tree.
        foreach (var file in ChangedFiles(repo, baseline))
        {
            if (policy.ForbidPaths.Any(p => file.Contains(p, StringComparison.Ordinal)))
            { Console.WriteLine($"FAIL: caminho proibido: {file}"); fail = true; break; }
        }

        // 3) Textos obrigatorios presentes nos fontes (fora bin/obj).
        foreach (var text in mustContain.Distinct())
        {
            if (GrepSources(repo, text) == 0)
            { Console.WriteLine($"FAIL: texto sumiu: {text}"); fail = true; }
        }

        // 4) Build do projeto (comando vem da politica; vazio = pula).
        if (!string.IsNullOrWhiteSpace(policy.Build))
        {
            string buildOut = RunBash(repo, policy.Build, TimeSpan.FromMinutes(5));
            if (!buildOut.Contains("Build succeeded", StringComparison.Ordinal))
            { Console.WriteLine("FAIL: build quebrou"); fail = true; }
        }

        Console.WriteLine(fail ? "GATE: FAIL" : "GATE: PASS");
        PrintMetrics();

        // Modo revisao: mostra o diff e so avanca a baseline (="push") com

        // Modo revisao: mostra o diff e so avanca a baseline (="push") com
        // aprovacao humana. Rejeitar desfaz os commits (soft: mantem arquivos).
        if (review && !fail)
        {
            Console.WriteLine("--- diff --stat baseline..HEAD ---");
            Console.WriteLine(Run(repo, "git", $"diff --stat {baseline} HEAD"));
            string full = Run(repo, "git", $"diff {baseline} HEAD");
            string[] diffLines = full.Split('\n');
            Console.WriteLine(string.Join('\n', diffLines.Take(120)));
            if (diffLines.Length > 120) Console.WriteLine($"... ({diffLines.Length - 120} linhas omitidas)");
            Console.Write("Aprovar push? [s/N] ");
            string? answer = Console.ReadLine();
            if (answer?.Trim().ToLowerInvariant() is "s" or "sim" or "y" or "yes")
            {
                File.WriteAllText(baselineFile, head + "\n");
                if (!string.IsNullOrWhiteSpace(policy.Push))
                {
                    Console.WriteLine($"push: {policy.Push}");
                    Console.WriteLine(RunBash(repo, policy.Push, TimeSpan.FromMinutes(5)));
                }
                Console.WriteLine("PUSH APROVADO (baseline avancada).");
                return 0;
            }
            Run(repo, "git", $"reset --soft {baseline}");
            Console.WriteLine("REJEITADO: commits desfeitos (arquivos mantidos p/ retrabalho).");
            return 1;
        }
        return fail ? 1 : 0;
    }

    /// <summary>Politica lida do gate.json (com padroes sensatos se ausente).</summary>
    sealed record Policy(string Build, bool RequireCommit, List<string> ForbidPaths,
        List<string> MustContain, string LogFile, string Push);

    /// <summary>Carrega gate.json; sem arquivo, usa deteccao (sln) + padroes.</summary>
    static Policy LoadPolicy(string path)
    {
        string build = "", log = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktop", "ciclo-dialogo-20261002.log");
        bool requireCommit = true;
        string push = "";
        var forbid = new List<string> { "bin/", "obj/" };
        var must = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (json is not null)
                {
                    build = json["build"]?.GetValue<string>() ?? "";
                    log = json["log"]?.GetValue<string>() ?? log;
                    requireCommit = json["requireCommit"]?.GetValue<bool>() ?? true;
                    push = json["push"]?.GetValue<string>() ?? "";
                    foreach (var p in json["forbidPaths"]?.AsArray() ?? new JsonArray())
                        if (p?.GetValue<string>() is string s) forbid.Add(s);
                    foreach (var m in json["mustContain"]?.AsArray() ?? new JsonArray())
                        if (m?.GetValue<string>() is string s) must.Add(s);
                }
            }
        }
        catch { /* politica invalida = padroes */ }

        // Sem build configurado: detecta solucao dotnet no repo.
        if (build == "")
        {
            string dir = Path.GetDirectoryName(path) ?? ".";
            string? sln = Directory.GetFiles(dir, "*.slnx").FirstOrDefault()
                ?? Directory.GetFiles(dir, "*.sln").FirstOrDefault();
            if (sln is not null) build = $"dotnet build \"{Path.GetFileName(sln)}\" -c Release --nologo";
        }
        return new Policy(build, requireCommit, forbid, must, log, push);
    }

    /// <summary>Roda o agente opencode pinado e devolve a saida (timeout = erro).</summary>
    static string RunAgent(string repo, string prompt)
    {
        string escaped = prompt.Replace("'", "'\\''");
        string cmd = "opencode run --standalone --thinking --auto --agent build --model openai/qwen3-8b-awq"
            + $" '{escaped}' >> \"{LogPath()}\" 2>&1; echo \"agent exit=$?\"";
        // Log direto no arquivo (dialogo completo preservado fora do stdout).
        string marker = Guid.NewGuid().ToString("N")[..8];
        string full = $"echo '=== GATE {marker} ===' >> \"{LogPath()}\" 2>&1; " + cmd;
        return RunBash(repo, full, TimeSpan.FromMinutes(9));
    }

    /// <summary>Caminho do log de dialogo.</summary>
    static string LogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Desktop", "ciclo-dialogo-20261002.log");

    /// <summary>Exibe tokens/s da bridge (best-effort) no console do e2e.</summary>
    static void PrintMetrics()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string json = http.GetStringAsync("http://127.0.0.1:4143/api/metrics")
                .GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            Console.WriteLine($"METRICS: req={r.GetProperty("requests")} "
                + $"in={r.GetProperty("input_tokens")} out={r.GetProperty("output_tokens")} "
                + $"tps_out={r.GetProperty("tps_output")} tps_total={r.GetProperty("tps_total")}");
        }
        catch { /* bridge fora do ar: metrica opcional */ }
    }

    /// <summary>Arquivos alterados baseline..HEAD + working tree (p/ caminhos proibidos).</summary>
    static List<string> ChangedFiles(string repo, string baseline)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Run(repo, "git", $"diff --name-only {baseline} HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            files.Add(f);
        foreach (var line in Run(repo, "git", "status --short")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string f = line.Length > 3 ? line[3..].Trim().Trim('"') : "";
            if (f != "") files.Add(f);
        }
        return files.ToList();
    }

    /// <summary>Arquivos do HEAD (para checar caminhos proibidos).</summary>
    static List<string> CommittedFiles(string repo) =>
        Run(repo, "git", "show --name-only --pretty=format: HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>Conta arquivos *.cs (fora bin/obj) contendo o texto.</summary>
    static int GrepSources(string repo, string text)
    {
        int hits = 0;
        foreach (var file in Directory.EnumerateFiles(repo, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (var line in File.ReadLines(file))
            {
                if (line.Contains(text, StringComparison.Ordinal)) { hits++; break; }
            }
        }
        return hits;
    }

    /// <summary>Roda executavel com args; "" se falhar/timeout.</summary>
    static string Run(string repo, string exe, string args, int timeoutMs = 30_000)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            if (!proc.WaitForExit(timeoutMs)) { try { proc.Kill(true); } catch { } return ""; }
            return proc.ExitCode == 0 ? proc.StandardOutput.ReadToEnd().Trim() : "";
        }
        catch { return ""; }
    }

    /// <summary>Roda bash -c com timeout; devolve stdout+stderr (mata arvore no estouro).</summary>
    static string RunBash(string repo, string script, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo("bash")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            using var proc = Process.Start(psi)!;
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(true); } catch { }
                return "TIMEOUT";
            }
            return proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        }
        catch (Exception ex) { return "erro: " + ex.Message; }
    }
}
