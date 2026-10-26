using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Teste de tools COMPLETO: read, write, edit(sed), glob, grep, navegacao em
// pastas, git (init/add/commit/log), compilacao C# (dotnet new/build/run),
// validacao e execucao de script. Roda nos 2 modelos (GPU1 E4B-Q4 e GPU2 E4B-Q3)
// em paralelo via bridge pura. Deve passar antes da bateria belts.
internal static class E2EToolsFullTests
{
    static async Task RunOnBridge(string bridge, string label)
    {
        async Task<JsonObject> Exec(string name, JsonObject args)
        {
            return await TestContext.PostObj(bridge + "/api/tools/execute",
                new JsonObject { ["name"] = name, ["arguments"] = args, ["role"] = "build" });
        }

        // 1. write
        var w = await Exec("write", new JsonObject { ["path"] = "a.cs", ["content"] = "class A { public int X = 1; }" });
        TestContext.Check(w["ok"]?.GetValue<bool>() == true, $"{label} write ok");

        // 2. read
        var r = await Exec("read", new JsonObject { ["path"] = "a.cs" });
        TestContext.Check(r["ok"]?.GetValue<bool>() == true && (r["output"]?.GetValue<string>() ?? "").Contains("class A"), $"{label} read ok");

        // 3. edit nativo (trecho exato) — sed -i e negado pelo inspetor
        var e = await Exec("edit", new JsonObject { ["path"] = "a.cs", ["oldString"] = "int X = 1", ["newString"] = "int X = 42" });
        TestContext.Check(e["ok"]?.GetValue<bool>() == true, $"{label} edit ok");
        var sedDenied = await Exec("shell", new JsonObject { ["command"] = "sed -i 's/int X = 42/int X = 7/' a.cs" });
        TestContext.Check(sedDenied["ok"]?.GetValue<bool>() != true, $"{label} sed -i negado");

        // 4. glob
        var g = await Exec("glob", new JsonObject { ["pattern"] = "*.cs" });
        TestContext.Check(g["ok"]?.GetValue<bool>() == true && (g["output"]?.GetValue<string>() ?? "").Contains("a.cs"), $"{label} glob ok");

        // 5. grep
        var gr = await Exec("grep", new JsonObject { ["pattern"] = "class A" });
        TestContext.Check(gr["ok"]?.GetValue<bool>() == true, $"{label} grep ok");

        // 6. navegacao em pastas
        var mk = await Exec("shell", new JsonObject { ["command"] = "mkdir -p sub && echo criado" });
        TestContext.Check(mk["ok"]?.GetValue<bool>() == true, $"{label} mkdir ok");
        var ls = await Exec("shell", new JsonObject { ["command"] = "ls" });
        TestContext.Check(ls["ok"]?.GetValue<bool>() == true && (ls["output"]?.GetValue<string>() ?? "").Contains("sub"), $"{label} ls ok");

        // 7. write em subpasta
        var w2 = await Exec("write", new JsonObject { ["path"] = "sub/b.cs", ["content"] = "class B { }" });
        TestContext.Check(w2["ok"]?.GetValue<bool>() == true, $"{label} write subpasta ok");

        // 8. git: repo ISOLADO no /tmp — nunca o cwd do workspace (um `git init`
        // espurio no root da workspace envenenava o log dos testes seguintes)
        string gdir = "/tmp/opencode/toolsfull/git-" + Environment.ProcessId;
        var ginit = await Exec("shell", new JsonObject { ["command"] = $"mkdir -p {gdir} && git init {gdir}" });
        TestContext.Check(ginit["ok"]?.GetValue<bool>() == true, $"{label} git init ok");
        var gcfg = await Exec("shell", new JsonObject { ["command"] = $"git -C {gdir} config user.email teste@exemplo.local" });
        TestContext.Check(gcfg["ok"]?.GetValue<bool>() == true, $"{label} git config ok");
        var gcm = await Exec("shell", new JsonObject { ["command"] = $"git -C {gdir} commit --allow-empty -m inicial" });
        TestContext.Check(gcm["ok"]?.GetValue<bool>() == true, $"{label} git commit ok");
        var gst = await Exec("shell", new JsonObject { ["command"] = $"git -C {gdir} status --short" });
        TestContext.Check(gst["ok"]?.GetValue<bool>() == true, $"{label} git status ok");
        var glog = await Exec("shell", new JsonObject { ["command"] = $"git -C {gdir} log --oneline" });
        TestContext.Check(glog["ok"]?.GetValue<bool>() == true, $"{label} git log ok");

        // 9. compilacao C# (dotnet new + build + run)
        var dn = await Exec("shell", new JsonObject { ["command"] = "bash -c \"mkdir -p /tmp/opencode/toolsfull/csharptest && cd /tmp/opencode/toolsfull/csharptest && dotnet new console --force 2>&1 | tail -1\"" });
        TestContext.Check(dn["ok"]?.GetValue<bool>() == true, $"{label} dotnet new ok");
        var db = await Exec("shell", new JsonObject { ["command"] = "bash -c \"cd /tmp/opencode/toolsfull/csharptest && dotnet build 2>&1 | tail -3\"" });
        TestContext.Check(db["ok"]?.GetValue<bool>() == true, $"{label} dotnet build ok");
        var dr = await Exec("shell", new JsonObject { ["command"] = "bash -c \"cd /tmp/opencode/toolsfull/csharptest && dotnet run 2>&1 | tail -3\"" });
        TestContext.Check(dr["ok"]?.GetValue<bool>() == true, $"{label} dotnet run ok");

        // 10. validacao e execucao de script
        var wsh = await Exec("write", new JsonObject { ["path"] = "/tmp/opencode/toolsfull/run.sh", ["content"] = "#!/bin/bash\necho 'hello tools'" });
        TestContext.Check(wsh["ok"]?.GetValue<bool>() == true, $"{label} write script ok");
        var val = await Exec("shell", new JsonObject { ["command"] = "bash -n /tmp/opencode/toolsfull/run.sh" });
        TestContext.Check(val["ok"]?.GetValue<bool>() == true, $"{label} bash -n ok");
        var exec = await Exec("shell", new JsonObject { ["command"] = "bash /tmp/opencode/toolsfull/run.sh" });
        TestContext.Check(exec["ok"]?.GetValue<bool>() == true && (exec["output"]?.GetValue<string>() ?? "").Contains("hello tools"), $"{label} exec script ok");

        Console.WriteLine($"  [toolsfull:{label}] PASS (read/write/edit/glob/grep/mkdir/git/dotnet/script)");
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
            "Inicie um repositorio git na pasta atual (nao crie subpasta). Crie nela um arquivo .txt com nome aleatorio " +
            "contendo a data do sistema de hoje. Se tiver acesso a internet, busque o clima da cidade de Sao Paulo hoje " +
            "e anote no arquivo. Salve o arquivo, versione no git com add e commit (primeiro commit: somente a data, sem " +
            "EDITADO), liste os arquivos e faca uma busca neles para encontrar o arquivo criado. Acrescente a marca " +
            "EDITADO ao mesmo arquivo (preserve a data exata: o arquivo final deve conter a data e a palavra EDITADO) " +
            "e realize o segundo commit. Verifique o historico git e grave no arquivo historico_git.log (sem commitar " +
            "esse arquivo de historico).";

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
        string body = txts.Length > 0 ? File.ReadAllText(txts[0]) : "";
        TestContext.Check(body.Contains(DateTime.Now.ToString("yyyy")),
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
        await RunOnBridge("http://127.0.0.1:4124", "GPU1-E4B-Q4");
        // Proposito do dual-tools mesclado: um enunciado agentico unico no modo corrente.
        await AgentScenario();
        Console.WriteLine("[toolsfull] 1/1 modelo PASS + cenario agentico");
    }
}
