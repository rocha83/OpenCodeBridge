// ===========================================================================
//  Rochas.OpenCodeBridge.Runner — console executor com allowlist.
//
//  FlaZero via API direta (sem harness do opencode): prompt curto, resposta
//  em voltas. Cada volta: chat -> extrai fence {"name","arguments"} -> valida
//  (allowlist 1o token + denylist de metacaracteres, SEM bash -c) -> executa
//  (argv direto, cwd travado, timeout, mata arvore) -> devolve stdout.
//
//  SEGURANCA: deny por padrao. python/dotnet/npm executam codigo arbitrario
//  por natureza — allowlist da higiene + auditoria, nao sandbox. sudo so por
//  match exato da lista SudoExact (live linux precisa; root solto, nunca).
//
//  CONVENCAO: identificadores en-US, comentarios pt-BR. Zero warnings.
// ===========================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Runner;

internal static partial class Program
{
    // Comandos liberados pelo 1o token (padrao; appsettings.json do console substitui).
    static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "echo", "sed", "grep", "find", "wc", "diff",
        "file", "pwd", "date", "git", "dotnet", "python", "python3", "node",
        "npm", "curl",
    };

    // Denylist de defesa em profundidade (sem shell ja bloqueia a maioria).
    static readonly string[] DeniedTokens =
    {
        "rm", "kill", "pkill", "killall", "reboot", "shutdown", "halt",
        "mkfs", "dd", "fdisk", "mount", "systemctl", "service", "crontab",
        "ssh", "scp", "wget", "chmod", "chown", "nohup", "setsid", "disown",
        "exec", "eval", "su", "mkfifo",
    };

    // systemctl/tee via sudo so nestes comandos inteiros exatos (appsettings substitui).
    static readonly List<string> SudoExact = new()
    {
        "systemctl restart opencode-bridge.service",
        "systemctl restart opencode-bridge-cpu.service",
        "systemctl status opencode-bridge.service",
        "systemctl status opencode-bridge-cpu.service",
    };

    // Escalares com padrao; appsettings ajusta, CLI vence o arquivo.
    static string CfgBridge = "http://127.0.0.1:4125";
    static string CfgModel = "qwen2.5-coder-3b-cpu";
    static string CfgLog = "/tmp/runner-exec.log";
    static string CfgTemp = "0.4";
    static string CfgVerbosity = "normal";
    static int CfgMaxTurns = 5;
    static int CfgTimeoutS = 120;

    /// <summary>appsettings.json ao lado do DLL: allow/sudoExact + escalares.</summary>
    static void LoadSettings()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return;
            var cfg = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (cfg is null) return;
            if (cfg["allow"]?.AsArray() is JsonArray allow && allow.Count > 0)
            {
                Allowed.Clear();
                foreach (var a in allow)
                    if (a?.GetValue<string>() is string s && s.Length > 0) Allowed.Add(s);
            }
            if (cfg["sudoExact"]?.AsArray() is JsonArray sudo && sudo.Count > 0)
            {
                SudoExact.Clear();
                foreach (var s in sudo)
                    if (s?.GetValue<string>() is string t && t.Length > 0) SudoExact.Add(t);
            }
            if (cfg["bridge"]?.GetValue<string>() is string b && b.Length > 0) CfgBridge = b;
            if (cfg["model"]?.GetValue<string>() is string m && m.Length > 0) CfgModel = m;
            if (cfg["logPath"]?.GetValue<string>() is string l && l.Length > 0) CfgLog = l;
            if (cfg["temperature"]?.GetValue<string>() is string tp && tp.Length > 0) CfgTemp = tp;
            if (cfg["verbosity"]?.GetValue<string>() is string v && v.Length > 0) CfgVerbosity = v.ToLowerInvariant();
            if (cfg["maxTurns"]?.GetValue<int>() is int turns && turns > 0) CfgMaxTurns = turns;
            if (cfg["timeoutSeconds"]?.GetValue<int>() is int sec && sec > 0) CfgTimeoutS = sec;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[runner] appsettings ignorado: {ex.Message}");
        }
    }

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("Uso: Runner exec \"<tarefa>\" [--bridge URL] [--model ID] [--repo DIR] [--max-turns N] [--timeout S] [--log PATH] [--verbosity quiet|normal|verbose]");
            return 0;
        }
        if (args[0] != "exec" || args.Length < 2) { Console.Error.WriteLine("Uso: Runner exec \"<tarefa>\" [...]"); return 1; }
        LoadSettings();
        string task = args[1];
        string bridge = Flag(args, "--bridge", CfgBridge);
        string model = Flag(args, "--model", CfgModel);
        string repo = Flag(args, "--repo", Directory.GetCurrentDirectory());
        int maxTurns = int.TryParse(Flag(args, "--max-turns", CfgMaxTurns.ToString()), out int mt) ? mt : CfgMaxTurns;
        int timeoutS = int.TryParse(Flag(args, "--timeout", CfgTimeoutS.ToString()), out int ts) ? ts : CfgTimeoutS;
        string logPath = Flag(args, "--log", CfgLog);
        string temp = Flag(args, "--temperature", CfgTemp);
        string verbosity = Flag(args, "--verbosity", CfgVerbosity).ToLowerInvariant();
        if (verbosity is not ("quiet" or "normal" or "verbose")) verbosity = "normal";
        return RunAsync(task, bridge.TrimEnd('/'), model, repo, maxTurns, timeoutS, logPath, verbosity, temp).GetAwaiter().GetResult();
    }

    static string Flag(string[] args, string name, string def)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name) return args[i + 1];
        return def;
    }

    /// <summary>Loop principal: chat -> fence -> valida -> executa -> devolve.</summary>
    /// <param name="verbosity">quiet (so resultado), normal (1 linha/volta), verbose (tudo).</param>
    static async Task<int> RunAsync(string task, string bridge, string model, string repo, int maxTurns, int timeoutS, string logPath, string verbosity, string temp)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "Voce opera comandos via allowlist. Responda com texto curto OU com exatamente um bloco ```json {\"name\": \"<comando>\", \"arguments\": \"<args>\"}```. Comandos validos: ls, cat, head, tail, echo, grep, find, wc, diff, file, pwd, git, dotnet, python, node, npm, curl. NAO existe pipe nem redirecionamento: para filtrar use * no proprio comando (ex. ls docs/screenshots/*e2e-cs*). NUNCA use bash/sh/sudo generico: chame o comando direto no name, sem flags no name. Para filtrar use * no proprio comando (ex. ls docs/screenshots/*e2e-cs*). NUNCA invente arquivo: confira existencia com ls primeiro. Para contar, liste tudo e conte as linhas voce mesmo. Quando terminar, responda so o resultado final, sem fence.",
            },
            new JsonObject { ["role"] = "user", ["content"] = task },
        };
        for (int turn = 1; turn <= maxTurns; turn++)
        {
            string text = await ChatAsync(bridge, model, messages, temp);
            if (!TryExtractCall(text, out string name, out string arguments))
            {
                Console.WriteLine(text);
                return 0;
            }
            if (!Validate(repo, name, arguments, out string why, out string[] argv))
            {
                Audit(logPath, "blocked", name, arguments, why, turn);
                Console.WriteLine($"BLOQUEADO (volta {turn}): {why}");
                if (verbosity == "verbose") Console.WriteLine($"[recebido] name='{name}' arguments='{Truncate(arguments, 200)}'");
                return 2;
            }
            string output = Execute(repo, argv, timeoutS);
            Audit(logPath, "exec", name, arguments, output, turn);
            string drift = GitDrift(repo);
            if (verbosity == "verbose")
                Console.WriteLine($"[volta {turn}] $ {name} {arguments}\n{Truncate(output, 2000)}{drift}");
            else if (verbosity == "normal")
                Console.WriteLine($"[volta {turn}] $ {name} {arguments} ({output.Length} bytes){FirstLine(drift)}");
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = text });
            int lines = output.Split('\n').Count(l => l.Trim().Length > 0);
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"Saida de `{name} {arguments}` (total: {lines} linhas):\n{Truncate(output, 4000)}\nProssiga ou de o resultado final." });
        }
        Console.WriteLine($"LIMITE de {maxTurns} voltas atingido.");
        return 1;
    }

    /// <summary>Chat direto sem stream (prompt curto, sem harness).</summary>
    static async Task<string> ChatAsync(string bridge, string model, JsonArray messages, string temp)
    {
        using var res = await Http.PostAsync(bridge + "/v1/chat/completions",
            new StringContent(new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages.DeepClone(),
                ["temperature"] = double.TryParse(temp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t) ? t : 0.4,
                ["max_tokens"] = 512,
                ["stream"] = false,
            }.ToJsonString(), Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();
        var obj = JsonNode.Parse(await res.Content.ReadAsStringAsync())?.AsObject();
        return obj?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
    }

    /// <summary>Extrai fence ```json {"name","arguments"} ou &lt;tool_call&gt;.</summary>
    static bool TryExtractCall(string text, out string name, out string arguments)
    {
        name = ""; arguments = "";
        var m = FenceRegex().Match(text);
        string json = m.Success ? m.Groups[1].Value : text.Trim();
        if (!m.Success)
        {
            var t = ToolTagRegex().Match(text);
            if (!t.Success) return false;
            json = t.Groups[1].Value;
        }
        try
        {
            var obj = JsonNode.Parse(json)?.AsObject();
            if (obj is null) return false;
            name = obj["name"]?.GetValue<string>()?.Trim() ?? "";
            var argNode = obj["arguments"];
            string argText = argNode is JsonValue v && v.TryGetValue<string>(out string? s) ? s : (argNode?.ToJsonString() ?? "");
            // 3B as vezes poe flags no name ("sed -i"): normaliza p/ 1o token.
            int sp = name.IndexOfAny(new[] { ' ', '\t' });
            if (sp > 0) { argText = (name[(sp + 1)..] + " " + argText).Trim(); name = name[..sp]; }
            arguments = argText;
            return name.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Valida allowlist 1o token + denylist + restricoes por comando.</summary>
    static bool Validate(string repo, string name, string arguments, out string why, out string[] argv)
    {
        why = ""; argv = Array.Empty<string>();
        // sudo so por match exato da lista fechada.
        if (name == "sudo")
        {
            string full = ("sudo " + arguments).Trim();
            if (SudoExact.Contains(full))
            {
                argv = new[] { "sudo" }.Concat(SplitArgs(arguments)).ToArray();
                return true;
            }
            why = "sudo fora da lista exata";
            return false;
        }
        if (!Allowed.Contains(name)) { why = $"comando '{name}' fora do allowlist"; return false; }
        string low = " " + arguments + " ";
        foreach (char c in new[] { ';', '&', '|', '>', '<', '$', '`', '!', '\r', '\n' })
            if (arguments.Contains(c)) { why = $"metacaractere '{c}' negado"; return false; }
        foreach (string bad in DeniedTokens)
            if (Regex.IsMatch(low, $@"[\s""']{Regex.Escape(bad)}[\s""']", RegexOptions.IgnoreCase))
            { why = $"token '{bad}' negado"; return false; }
        string[] parts = SplitArgs(arguments);
        if (!ExtraRules(name, parts, out why)) return false;
        argv = new[] { name }.Concat(ExpandGlobs(repo, parts)).ToArray();
        return true;
    }

    /// <summary>Restricoes por comando (subcomando permitido, flags perigosas).</summary>
    static bool ExtraRules(string name, string[] parts, out string why)
    {
        why = "";
        switch (name)
        {
            case "git":
                if (parts.Length == 0 || !"status diff log show branch".Split(' ').Contains(parts[0]))
                { why = "git: so status/diff/log/show/branch"; return false; }
                break;
            case "dotnet":
                if (parts.Length == 0 || !"build test".Split(' ').Contains(parts[0]))
                { why = "dotnet: so build/test"; return false; }
                break;
            case "npm":
                if (parts.Length == 0 || !"run test build".Split(' ').Contains(parts[0]))
                { why = "npm: so run/test/build (sem install)"; return false; }
                break;
            case "python":
            case "python3":
            case "node":
                if (parts.Length == 0 || parts[0] is "-c" or "-e")
                { why = $"{name}: so script de arquivo (sem -c/-e)"; return false; }
                break;
            case "tail":
                if (parts.Contains("-f")) { why = "tail: sem -f (bloqueia)"; return false; }
                break;
            case "curl":
                if (parts.Contains("-X") || parts.Contains("--request") || parts.Contains("-d") || parts.Contains("--data"))
                { why = "curl: so GET"; return false; }
                break;
        }
        return true;
    }

    /// <summary>Split simples respeitando aspas (sem shell).</summary>
    static string[] SplitArgs(string arguments)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        foreach (char c in arguments.Trim())
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else cur.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); }
            }
            else
            {
                cur.Append(c);
            }
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts.ToArray();
    }

    /// <summary>Expande * ? [...] do proprio Runner (sem shell, relativo ao repo).</summary>
    static string[] ExpandGlobs(string repo, string[] parts)
    {
        var expanded = new List<string>();
        foreach (string p in parts)
        {
            if (p.IndexOfAny(new[] { '*', '?', '[' }) < 0) { expanded.Add(p); continue; }
            try
            {
                string dir = Path.GetDirectoryName(p) ?? ".";
                if (dir.Length == 0) dir = ".";
                string absDir = Path.Combine(repo, dir);
                string[] hits = Directory.GetFileSystemEntries(absDir, Path.GetFileName(p));
                if (hits.Length == 0) expanded.Add(p);
                else expanded.AddRange(hits.OrderBy(h => h).Select(h => Path.GetRelativePath(repo, h)));
            }
            catch { expanded.Add(p); }
        }
        return expanded.ToArray();
    }

    /// <summary>Executa argv direto (sem shell), cwd travado, timeout, mata arvore.</summary>
    static string Execute(string repo, string[] argv, int timeoutS)
    {
        try
        {
            var psi = new ProcessStartInfo(argv[0])
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (string a in argv.Skip(1)) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return "ERRO: processo nao iniciou";
            try { proc.StandardInput.Close(); }
            catch { /* stdin EOF: sem pipe, comando sem arquivo nao pendura */ }
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            if (!proc.WaitForExit(timeoutS * 1000))
            {
                KillTree(proc.Id);
                proc.WaitForExit(5000);
                return "TIMEOUT (arvore morta via kill interno):\n" + Truncate(stdout.ToString().Trim(), 1000);
            }
            string err = stderr.ToString().Trim();
            return stdout.ToString().Trim() + (err.Length > 0 ? "\n[stderr]\n" + err : "");
        }
        catch (Exception ex)
        {
            return "ERRO: " + ex.Message;
        }
    }

    /// <summary>Drift do repo apos o comando ("" se nao for repo git).</summary>
    static string GitDrift(string repo)
    {
        if (Git(repo, "rev-parse --is-inside-work-tree") != "true") return "";
        string stat = Git(repo, "diff --stat");
        string untracked = Git(repo, "status --short");
        int files = untracked.Split('\n').Count(l => l.Trim().Length > 0);
        string head = $"\n[drift] {stat.Split('\n').Count(l => l.Contains('|'))} arquivos, {files} untracked";
        if (stat.Length > 0 && stat.Length < 3000)
            return head + "\n" + stat + (stat.Length < 1500 ? "\n" + Truncate(Git(repo, "diff"), 1500) : "");
        return stat.Length > 0 ? head + "\n" + Truncate(stat, 500) : head;
    }

    /// <summary>Roda git interno (confiavel, fora do allowlist do modelo).</summary>
    static string Git(string repo, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = repo,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (string a in args.Split(' ')) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return "";
            try { proc.StandardInput.Close(); } catch { }
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            return output.Trim();
        }
        catch { return ""; }
    }

    static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    /// <summary>Mata a arvore em 3 niveis (interno do Runner, fora do allowlist do modelo).</summary>
    static void KillTree(int pid)
    {
        try { Process.GetProcessById(pid).Kill(true); } catch { }
        if (Alive(pid)) RunInternal("pkill", new[] { "-9", "-P", pid.ToString() });
        if (Alive(pid)) RunInternal("kill", new[] { "-9", pid.ToString() });
        if (Alive(pid)) RunInternal("sudo", new[] { "kill", "-9", pid.ToString() });
        if (Alive(pid)) RunInternal("sudo", new[] { "pkill", "-9", "-P", pid.ToString() });
    }

    /// <summary>Processo ainda vivo (best-effort).</summary>
    static bool Alive(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    /// <summary>Roda binario interno de kill (caminho confiavel, nao vem do modelo).</summary>
    static void RunInternal(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            if (proc is null) return;
            try { proc.StandardInput.Close(); } catch { }
            proc.WaitForExit(5000);
            try { if (!proc.HasExited) proc.Kill(); } catch { }
        }
        catch { /* kill ausente ou sem permissao: best-effort */ }
    }

    static void Audit(string logPath, string evt, string command, string arguments, string output, int turn)
    {
        try
        {
            Rotate(logPath);
            File.AppendAllText(logPath, new JsonObject
            {
                ["event"] = evt,
                ["turn"] = turn,
                ["command"] = command,
                ["arguments"] = Truncate(arguments, 300),
                ["output_bytes"] = output.Length,
                ["output_head"] = Truncate(output, 1000),
                ["drift_stat"] = Truncate(drift, 500),
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }.ToJsonString() + "\n");
        }
        catch { /* auditoria best-effort, nunca quebra a execucao */ }
    }

    /// <summary>Rotacao simples: acima de 20 MB, vira .1 (1 backup).</summary>
    static void Rotate(string logPath)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (info.Exists && info.Length > 20_000_000)
                File.Move(logPath, logPath + ".1", true);
        }
        catch { /* best-effort */ }
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n[truncado]";

    [GeneratedRegex("```(?:json)?\\s*(\\{.*?\\})\\s*```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    [GeneratedRegex("<tool_call>(.*?)</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolTagRegex();
}
