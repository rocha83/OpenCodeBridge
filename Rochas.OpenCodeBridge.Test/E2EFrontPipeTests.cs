// ===========================================================================
//  Frontpipe E2E (front MVC + React + Bootstrap + Tailwind pelos modelos):
//    12B (bridge GPU) GERA os 3 artefatos no workspace;
//    E2B (bridge CPU, role exec) AJUSTA pelos enunciados (frontpipe/enunciados/*.md);
//    a suite MEDE os estilos computados no navegador (Playwright C# via
//    Rochas.OpenCodeBridge.Web.Test --front-measure) e exige delta/no-regressao.
//  Fases (rodaveis isoladas com --only E-front-*): gen -> edit -> verify.
//  Dumps para auditoria: <ws>/gen, <ws>/edit/<id>, <ws>/reports (ws=/tmp/opencode/frontpipe).
// ===========================================================================

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Test;

internal static class E2EFrontPipeTests
{
    const string CpuBridge = "http://127.0.0.1:4125";
    const string Url = "http://127.0.0.1:5199/";
    const string AppName = "FrontApp";
    const string CssRel = "app/wwwroot/css/app.css";

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    static HttpClient? ping;

    static string Ws => Path.Combine(Path.GetTempPath(), "opencode", "frontpipe");
    static string App => Path.Combine(Ws, "app");
    static string Css => Path.Combine(App, "wwwroot", "css", "app.css");
    static string IndexView => Path.Combine(App, "Views", "Home", "Index.cshtml");
    static string AppJs => Path.Combine(App, "wwwroot", "js", "app.js");

    // ------------------------------------------------------------------ helpers
    static string RepoDir(string rel)
    {
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            string p = Path.Combine(probe.FullName, rel);
            if (Directory.Exists(p)) return p;
            probe = probe.Parent;
        }
        throw new Exception("diretorio do repo nao encontrado: " + rel);
    }

    static void Append(string file, string line)
    {
        try { File.AppendAllText(file, line + "\n"); } catch { }
    }

    static string Clip(string s, int n = 4000)
    {
        s ??= "";
        return s.Length <= n ? s : s[..n] + $"\n[...truncado {s.Length - n} chars]";
    }

    static double Num(JsonNode? n)
    {
        if (n is null) return double.NaN;
        string s = n.GetValue<string>() ?? "";
        var m = Regex.Match(s, @"-?\d+(\.\d+)?");
        return m.Success && double.TryParse(m.Value, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    static (int Code, string Out) Run(string exe, string cwd, int secs, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi) ?? throw new Exception("sem processo: " + exe);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout " + exe + " " + string.Join(' ', args)); }
        return (p.ExitCode, so.Result + se.Result);
    }

    static string Shell(string cwd, string cmd, int secs = 180)
    {
        var psi = new ProcessStartInfo("/bin/bash") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(cmd);
        using var p = Process.Start(psi) ?? throw new Exception("sem processo: bash");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } return "TIMEOUT."; }
        return $"exit={p.ExitCode}\n" + so.Result + se.Result;
    }

    // ---- instalacao do app (deterministica, nao e' o modelo que faz o dotnet new)
    static readonly (string File, string Sub, string Url)[] AssetMap =
    {
        ("react.production.min.js", "js", "https://cdn.jsdelivr.net/npm/react@18.3.1/umd/react.production.min.js"),
        ("react-dom.production.min.js", "js", "https://cdn.jsdelivr.net/npm/react-dom@18.3.1/umd/react-dom.production.min.js"),
        ("tailwind.js", "js", "https://cdn.tailwindcss.com/3.4.16"),
        ("bootstrap.min.css", "css", "https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css"),
    };

    static void SetupApp()
    {
        Directory.CreateDirectory(Ws);
        if (!File.Exists(Path.Combine(App, AppName + ".csproj")))
        {
            if (Directory.Exists(App)) Directory.Delete(App, true);
            TestContext.ScaffoldProject(AppName, App, kind: "mvc");   // template `dotnet new mvc`
            Console.WriteLine($"  [front] scaffold {AppName} (dotnet new mvc) em {App}");
        }
        string repoAssets = RepoDir("frontpipe/assets");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        foreach (var (file, sub, url) in AssetMap)
        {
            string dest = Path.Combine(App, "wwwroot", sub, file);
            if (File.Exists(dest) && new FileInfo(dest).Length > 1000) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            string vendored = Path.Combine(repoAssets, file);
            if (File.Exists(vendored)) File.Copy(vendored, dest, true);
            else File.WriteAllBytes(dest, http.GetByteArrayAsync(url).GetAwaiter().GetResult());
            Console.WriteLine($"  [front] asset {sub}/{file} ({new FileInfo(dest).Length} bytes)");
        }
    }

    static string Enunciado(string file)
    {
        string p = Path.Combine(RepoDir("frontpipe/enunciados"), file);
        TestContext.Check(File.Exists(p), "enunciado ausente: " + p);
        return File.ReadAllText(p).Replace("{{WS}}", Ws);
    }

    static JsonArray LoadEdits() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(RepoDir("frontpipe/enunciados"), "edits.json")))!.AsArray();

    static JsonObject LoadJson(string path, string hint)
    {
        TestContext.Check(File.Exists(path), $"snapshot ausente: {path} ({hint})");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    static string Abs(string text) => text.Replace("app/", Ws + "/app/");

    // ------------------------------------------------------------------- servidor
    static Process? Server;

    static bool Ping()
    {
        ping ??= new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try { using var r = ping.GetAsync(Url).GetAwaiter().GetResult(); return (int)r.StatusCode < 500; }
        catch { return false; }
    }

    static void StopServer()
    {
        if (Server is { HasExited: false }) { try { Server.Kill(true); } catch { } Server.WaitForExit(10000); }
        Server = null;
        string pidFile = Path.Combine(Ws, "serve.pid");
        if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int pid))
        {
            try { Process.GetProcessById(pid).Kill(true); } catch { }
            File.Delete(pidFile);
        }
    }

    static void EnsureServer()
    {
        if (Ping()) return;
        string dll = Directory.GetFiles(Path.Combine(App, "bin"), AppName + ".dll", SearchOption.AllDirectories)
            .OrderByDescending(f => f.Contains("Release"))
            .FirstOrDefault() ?? throw new Exception("app nao buildado (rode E-front-gen antes)");
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = App, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add(Url.TrimEnd('/'));
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        Server = Process.Start(psi) ?? throw new Exception("sem processo do app");
        string log = Path.Combine(Ws, "serve.log");
        Server.OutputDataReceived += (_, e) => { if (e.Data is not null) Append(log, e.Data); };
        Server.ErrorDataReceived += (_, e) => { if (e.Data is not null) Append(log, e.Data); };
        Server.BeginOutputReadLine();
        Server.BeginErrorReadLine();
        File.WriteAllText(Path.Combine(Ws, "serve.pid"), Server.Id.ToString());
        for (int i = 0; i < 120; i++)
        {
            if (Ping()) { Console.WriteLine($"  [front] app no ar em {Url} (pid {Server.Id})"); return; }
            if (Server.HasExited) throw new Exception("app morreu ao subir: " + Clip(File.Exists(log) ? File.ReadAllText(log) : "", 600));
            Thread.Sleep(1000);
        }
        throw new Exception("timeout subindo o app em " + Url);
    }

    // Medicao real no navegador (Playwright C# do Web.Test, mesmo padrao das belts).
    static JsonObject Measure(string outAbs)
    {
        string dll = FrontMeasureDll();
        var (code, outp) = Run("dotnet", Ws, 600, "exec", dll, "--front-measure", "--front-url", Url, "--front-out", outAbs);
        string last = outp.Trim().Split('\n').LastOrDefault() ?? "";
        Console.WriteLine($"  [front] measure exit={code}: {Clip(last, 220)}");
        return LoadJson(outAbs, "medicao Playwright");
    }

    static string FrontMeasureDll()
    {
        string? env = Environment.GetEnvironmentVariable("FRONT_MEASURE_DLL");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            string bin = Path.Combine(probe.FullName, "Rochas.OpenCodeBridge.Web.Test", "bin", "Release");
            if (Directory.Exists(bin))
            {
                string? hit = Directory.GetFiles(bin, "Rochas.OpenCodeBridge.Web.Test.dll", SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
            probe = probe.Parent;
        }
        TestContext.Skip("Web.Test nao buildado (dotnet build Rochas.OpenCodeBridge.Web.Test -c Release): medicao Playwright indisponivel");
        throw new Exception("inalcancavel");
    }

    // ------------------------------------------------------------------- tools locais
    static JsonObject P(string name, string desc, JsonObject props, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = req },
            },
        };
    }

    static JsonArray FrontTools(string[] names)
    {
        var all = new Dictionary<string, JsonObject>
        {
            ["read_file"] = P("read_file", "Le um arquivo do workspace (offset/limit em linhas, opcionais)",
                new JsonObject { ["path"] = new JsonObject { ["type"] = "string" }, ["offset"] = new JsonObject { ["type"] = "integer" }, ["limit"] = new JsonObject { ["type"] = "integer" } }, "path"),
            ["list_dir"] = P("list_dir", "Lista um diretorio do workspace",
                new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } }, "path"),
            ["write_file"] = P("write_file", "Cria/substitui um arquivo texto do workspace",
                new JsonObject { ["path"] = new JsonObject { ["type"] = "string" }, ["content"] = new JsonObject { ["type"] = "string" } }, "path", "content"),
            ["edit_file"] = P("edit_file", "Troca old_string por new_string num arquivo do workspace (old_string deve ser unico)",
                new JsonObject { ["path"] = new JsonObject { ["type"] = "string" }, ["old_string"] = new JsonObject { ["type"] = "string" }, ["new_string"] = new JsonObject { ["type"] = "string" } }, "path", "old_string", "new_string"),
        };
        var arr = new JsonArray();
        foreach (var n in names) if (all.TryGetValue(n, out var t)) arr.Add(t.DeepClone());
        return arr;
    }

    static bool Allowed(string rel, string[] allow) =>
        allow.Length == 0 || allow.Any(a => rel == a || a.EndsWith("/**") && rel.StartsWith(a[..^3], StringComparison.Ordinal));

    static string LocalExec(string[] allow, string fn, JsonObject args)
    {
        try
        {
            string Arg(string k) => args[k]?.GetValue<string>() ?? "";
            string Resolve(string p)
            {
                string abs = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(Ws, p));
                if (!abs.StartsWith(Ws, StringComparison.Ordinal)) throw new Exception("path fora do workspace: " + p);
                string rel = Path.GetRelativePath(Ws, abs).Replace('\\', '/');
                if (!Allowed(rel, allow)) throw new Exception($"path fora do escopo do enunciado ({rel}; permitido: {string.Join(",", allow)})");
                return abs;
            }
            switch (fn)
            {
                case "read_file":
                {
                    string abs = Resolve(Arg("path"));
                    if (!File.Exists(abs)) return "ERRO: arquivo nao existe: " + Arg("path");
                    var lines = File.ReadAllLines(abs);
                    int off = args["offset"]?.GetValue<int>() ?? 0;
                    int lim = args["limit"]?.GetValue<int>() ?? lines.Length;
                    off = Math.Clamp(off, 0, Math.Max(0, lines.Length));
                    var slice = lines.Skip(off).Take(Math.Max(0, lim)).Select((l, i) => $"{off + i + 1}: {l}");
                    return Clip($"{Arg("path")} ({lines.Length} linhas)\n" + string.Join("\n", slice));
                }
                case "list_dir":
                {
                    string abs = Resolve(Arg("path").Length == 0 ? "." : Arg("path"));
                    return Clip(string.Join("\n", Directory.GetFileSystemEntries(abs)
                        .OrderBy(x => x)
                        .Select(x => Path.GetFileName(x) + (Directory.Exists(x) ? "/" : $" ({new FileInfo(x).Length}b)"))));
                }
                case "write_file":
                {
                    string abs = Resolve(Arg("path"));
                    Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                    File.WriteAllText(abs, Arg("content"));
                    return $"escrito {Path.GetRelativePath(Ws, abs)} ({Arg("content").Length} bytes)";
                }
                case "edit_file":
                {
                    string abs = Resolve(Arg("path"));
                    if (!File.Exists(abs)) return "ERRO: arquivo nao existe: " + Arg("path");
                    string src = File.ReadAllText(abs);
                    string oldS = Arg("old_string"), newS = Arg("new_string");
                    if (oldS.Length == 0) return "ERRO: old_string vazio";
                    int at = src.IndexOf(oldS, StringComparison.Ordinal);
                    if (at < 0) return "ERRO: old_string nao encontrado em " + Arg("path");
                    if (src.IndexOf(oldS, at + 1, StringComparison.Ordinal) >= 0) return "ERRO: old_string ambiguo (mais de 1 ocorrencia)";
                    File.WriteAllText(abs, src[..at] + newS + src[(at + oldS.Length)..]);
                    return $"editado {Path.GetRelativePath(Ws, abs)} ({oldS.Length} -> {newS.Length} chars)";
                }
                default:
                    return "TOOL DESCONHECIDA: " + fn;
            }
        }
        catch (Exception ex) { return "ERRO: " + ex.Message; }
    }

    // ------------------------------------------------------------------ loop do modelo
    static async Task<JsonObject> Chat(string bridge, string model, JsonArray messages, JsonArray? tools)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.3,
            ["stream"] = false,
            ["messages"] = messages.DeepClone(),
        };
        if (tools is { Count: > 0 }) body["tools"] = tools.DeepClone();
        return await TestContext.PostObj(bridge + "/v1/chat/completions", body);
    }

    static async Task<JsonObject> AgentLoop(string bridge, string model, string system, string prompt,
        string[] allow, string[] toolNames, int maxTurns, string dumpAbs, string label)
    {
        var tools = FrontTools(toolNames);
        var history = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = prompt });
        var turns = new JsonArray();
        double tokensOut = 0, secs = 0;
        string final = "";
        int exit = 3;
        Directory.CreateDirectory(Path.GetDirectoryName(dumpAbs)!);

        for (int turn = 1; turn <= maxTurns; turn++)
        {
            var t0 = DateTime.UtcNow;
            var r = await Chat(bridge, model, (JsonArray)history.DeepClone(), tools);
            double dt = (DateTime.UtcNow - t0).TotalSeconds;
            var msg = r["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("resposta sem message");
            double outTok = r["usage"]?["output_tokens"]?.GetValue<double>() ?? r["usage"]?["completion_tokens"]?.GetValue<double>() ?? 0;
            tokensOut += outTok;
            secs += dt;
            string content = msg["content"]?.GetValue<string>() ?? "";
            var calls = msg["tool_calls"]?.AsArray();
            var rec = new JsonObject
            {
                ["turn"] = turn,
                ["seconds"] = Math.Round(dt, 1),
                ["out_tokens"] = (int)outTok,
                ["tps"] = dt > 0 ? Math.Round(outTok / dt, 2) : 0,
                ["content"] = Clip(content, 2000),
            };
            turns.Add(rec);
            string names = calls is null ? "" : string.Join(",", calls.Select(c => c?["function"]?["name"]?.GetValue<string>() ?? "?"));
            Console.WriteLine($"  [front:{label}] turno {turn}: {dt:F1}s {outTok} tok tool=[{names}]");

            if (calls is null || calls.Count == 0)
            {
                final = content;
                exit = Regex.IsMatch(content, @"\[tool_call\]|tool_call>|(?m)^\s*(read_file|write_file|edit_file|list_dir)\s*\(") ? 4 : 0;
                break;
            }

            var echo = new JsonArray();
            foreach (var c in calls)
                echo.Add(new JsonObject
                {
                    ["id"] = c?["id"]?.GetValue<string>() ?? "",
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}",
                    },
                });
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = content, ["tool_calls"] = echo });

            var results = new JsonArray();
            foreach (var c in calls)
            {
                string name = c?["function"]?["name"]?.GetValue<string>() ?? "";
                JsonObject a;
                try { a = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject(); }
                catch { a = new JsonObject(); }
                string res = LocalExec(allow, name, a);
                results.Add(new JsonObject { ["name"] = name, ["args"] = Clip(a.ToJsonString(), 400), ["result"] = Clip(res, 800) });
                history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = Clip(res) });
            }
            rec["tools"] = results;
        }

        var summary = new JsonObject
        {
            ["label"] = label,
            ["bridge"] = bridge,
            ["model"] = model,
            ["turns"] = turns.Count,
            ["out_tokens"] = (int)tokensOut,
            ["model_seconds"] = Math.Round(secs, 1),
            ["tps"] = secs > 0 ? Math.Round(tokensOut / secs, 2) : 0,
            ["exit_code"] = exit,
            ["final_text"] = Clip(final, 2000),
            ["transcript"] = turns,
        };
        File.WriteAllText(dumpAbs, summary.ToJsonString(Indented));
        if (exit == 4) Console.WriteLine($"  [front:{label}] AVISO: tool call escrita em prosa (nao executavel)");
        return summary;
    }

    static async Task<string> E2bModel()
    {
        try
        {
            var st = await TestContext.GetObj(CpuBridge + "/api/status");
            string m = st["model"]?.GetValue<string>() ?? "gemma-4-e2b";
            return m.EndsWith("-exec") ? m : m + "-exec";
        }
        catch { return "gemma-4-e2b-exec"; }
    }

    static async Task<JsonObject> StandalonePhase(string agent, string prompt, string dumpAbs, string label)
    {
        TestContext.StandaloneAgent = agent;
        string outp = TestContext.OpencodeRun(Abs(prompt));
        var sum = new JsonObject
        {
            ["label"] = label,
            ["standalone"] = true,
            ["agent"] = agent,
            ["output"] = Clip(outp, 3000),
            ["tps"] = 0,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(dumpAbs)!);
        File.WriteAllText(dumpAbs, sum.ToJsonString(Indented));
        return sum;
    }

    // ================================================================== E-front-gen
    public static async Task Gen()
    {
        string model = TestContext.SuiteModel;
        if (!model.EndsWith("-build")) model += "-build";
        Console.WriteLine($"[front:gen] modelo {model} (bridge {TestContext.Bridge}), workspace {Ws}");
        Directory.CreateDirectory(Ws);
        StopServer();
        SetupApp();

        const string sys = "Voce e dev front-end senior (React 18 sem JSX + ASP.NET Core MVC). " +
            "Responda SOMENTE com tool calls estruturadas, uma por resposta; jamais escreva a chamada como texto. " +
            "Caminhos das tools sao relativos a raiz do workspace. Ao concluir os 3 arquivos, responda apenas FRONTPIPE-GEN-OK.";
        string prompt = Enunciado("gen.md");
        string dump = Path.Combine(Ws, "gen", "transcript.json");
        var sum = TestContext.Standalone
            ? await StandalonePhase("build", prompt, dump, "gen")
            : await AgentLoop(TestContext.Bridge, model, sys, prompt,
                new[] { "app/Views/Home/Index.cshtml", "app/wwwroot/js/app.js", CssRel },
                new[] { "write_file", "read_file", "edit_file", "list_dir" }, 16, dump, "gen");

        TestContext.Check(File.Exists(IndexView) && File.Exists(AppJs) && File.Exists(Css),
            "os 3 artefatos foram escritos pelo modelo (Index.cshtml, app.js, app.css)");
        string idx = File.ReadAllText(IndexView), js = File.ReadAllText(AppJs), css = File.ReadAllText(Css);
        TestContext.Check(idx.Contains("id=\"root\""), "Index.cshtml monta o #root do React");
        TestContext.Check(idx.Contains("react.production.min.js") && idx.Contains("react-dom.production.min.js")
            && idx.Contains("app.js"), "Index.cshtml carrega react, react-dom e app.js");
        TestContext.Check(idx.Contains("bootstrap.min.css") && idx.Contains("app.css") && idx.Contains("tailwind.js"),
            "Index.cshtml carrega bootstrap, app.css e tailwind");
        TestContext.Check(js.Contains("createElement") && js.Contains("createRoot"),
            "app.js e' React sem JSX (createElement + createRoot)");
        TestContext.Check(Regex.IsMatch(css, @"#quadro\s*\{[^}]*width\s*:", RegexOptions.Singleline), "app.css define width do #quadro");
        TestContext.Check(Regex.IsMatch(css, @"#titulo\s*\{[^}]*font-size\s*:", RegexOptions.Singleline), "app.css define font-size do #titulo");

        // Dump dos artefatos gerados (auditoria).
        string genDir = Path.Combine(Ws, "gen");
        Directory.CreateDirectory(genDir);
        File.Copy(IndexView, Path.Combine(genDir, "Index.cshtml"), true);
        File.Copy(AppJs, Path.Combine(genDir, "app.js"), true);
        File.Copy(Css, Path.Combine(genDir, "app.css"), true);
        File.WriteAllText(Path.Combine(genDir, "gen.enunciado.md"), prompt);

        // ACEITE: o front tem que buildar (integro) antes de servir.
        var (code, buildOut) = Run("dotnet", App, 900, "build", "-c", "Release", "--nologo", "-v", "q");
        TestContext.Check(code == 0 && (buildOut.Contains("Build succeeded") || buildOut.Contains("êxito") || buildOut.Contains("succeeded")),
            "app builda (dotnet build -c Release): " + Clip(buildOut.Split('\n')[0], 200));

        EnsureServer();
        var before = Measure(Path.Combine(genDir, "before.json"));
        CheckSnapshot("baseline", before, requireQuadro: true);
        double fs = Num(before["styles"]?["#titulo"]?["font-size"]);
        double qw = Num(before["styles"]?["#quadro"]?["width"]);
        TestContext.Check(fs >= 8 && qw >= 50, $"baseline aplicada pelo CSS (titulo {fs}px, quadro {qw}px)");
        Console.WriteLine($"  [front:gen] {sum["model"]}: {sum["out_tokens"]} tok em {sum["model_seconds"]}s = {sum["tps"]} tok/s");
    }

    // ================================================================= E-front-edit
    public static async Task Edit()
    {
        TestContext.Check(File.Exists(Css), "rode E-front-gen antes (workspace sem app.css)");
        // Volta ao baseline do gen (idempotente em re-run) e re-mede o "before".
        string genCss = Path.Combine(Ws, "gen", "app.css");
        if (File.Exists(genCss)) File.Copy(genCss, Css, true);
        EnsureServer();
        var before = Measure(Path.Combine(Ws, "gen", "before.json"));
        CheckSnapshot("baseline", before, requireQuadro: true);

        string model = await E2bModel();
        var edits = LoadEdits();
        Console.WriteLine($"[front:edit] modelo {model} (bridge {CpuBridge}), {edits.Count} enunciados de ajuste");
        const string sys = "Voce e o modelo de edicao de CSS (role exec). Trabalhe apenas no arquivo do enunciado. " +
            "Use uma tool call por resposta: primeiro read_file para ver o conteudo, depois edit_file no trecho exato. " +
            "Caminhos das tools sao relativos a raiz do workspace. Ao terminar, responda apenas OK.";

        foreach (var e in edits)
        {
            var rule = e!.AsObject();
            string id = rule["id"]!.GetValue<string>();
            string file = rule["file"]!.GetValue<string>();
            string prompt = Enunciado(file);
            string pre = File.ReadAllText(Css);
            string dump = Path.Combine(Ws, "edit", id, "transcript.json");
            var sum = TestContext.Standalone
                ? await StandalonePhase("exec", prompt, dump, id)
                : await AgentLoop(CpuBridge, model, sys, prompt, new[] { CssRel },
                    new[] { "read_file", "write_file", "edit_file" }, 8, dump, id);

            string post = File.ReadAllText(Css);
            string editDir = Path.Combine(Ws, "edit", id);
            Directory.CreateDirectory(editDir);
            File.Copy(Css, Path.Combine(editDir, "app.css"), true);
            File.WriteAllText(Path.Combine(editDir, "enunciado.md"), prompt);
            int diff = DiffLines(pre, post);
            TestContext.Check(diff <= 3, $"{id}: mexeu so no trecho do enunciado ({diff} linha(s) alterada(s), max 3)");
            TestContext.Check(Regex.IsMatch(post, @"#quadro\s*\{[^}]*width\s*:", RegexOptions.Singleline)
                && Regex.IsMatch(post, @"#titulo\s*\{[^}]*font-size\s*:", RegexOptions.Singleline),
                $"{id}: CSS continua com #quadro e #titulo definidos");
            Console.WriteLine($"  [front:edit] {id}: {sum["out_tokens"]} tok em {sum["model_seconds"]}s = {sum["tps"]} tok/s, linhas alteradas={diff}");

            var after = Measure(Path.Combine(editDir, "after.json"));
            CheckSnapshot(id, after, requireQuadro: true);
        }
    }

    static int DiffLines(string a, string b)
    {
        string[] la = a.Replace("\r\n", "\n").Split('\n');
        string[] lb = b.Replace("\r\n", "\n").Split('\n');
        int d = 0;
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
            if ((i < la.Length ? la[i] : null) != (i < lb.Length ? lb[i] : null)) d++;
        return d;
    }

    static void CheckSnapshot(string step, JsonObject snap, bool requireQuadro)
    {
        TestContext.Check(snap["http_status"]?.GetValue<int>() == 200, $"{step}: pagina responde 200 ({snap["url"]})");
        TestContext.Check((snap["dom"]?["root_children"]?.GetValue<int>() ?? 0) >= 1, $"{step}: React montou no #root");
        TestContext.Check((snap["console_errors"]?.AsArray().Count ?? 0) == 0,
            $"{step}: sem erro de console na pagina: {snap["console_errors"]?.ToJsonString()}");
        if (requireQuadro)
        {
            TestContext.Check(snap["styles"]?["#titulo"] is not null && snap["styles"]?["#quadro"] is not null,
                $"{step}: #titulo e #quadro renderizados");
            string links = snap["links"]?.ToJsonString() ?? "";
            TestContext.Check(links.Contains("app.css") && links.Contains("app.js"), $"{step}: assets do front continuam carregados");
        }
    }

    // =============================================================== E-front-verify
    public static Task Verify()
    {
        var edits = LoadEdits();
        string reports = Path.Combine(Ws, "reports");
        Directory.CreateDirectory(reports);
        var chain = new List<(string Id, JsonObject Snap)> { ("baseline", LoadJson(Path.Combine(Ws, "gen", "before.json"), "rode E-front-gen")) };
        foreach (var e in edits)
        {
            string id = e!["id"]!.GetValue<string>();
            chain.Add((id, LoadJson(Path.Combine(Ws, "edit", id, "after.json"), $"rode E-front-edit ({id})")));
        }

        var verdicts = new JsonArray();
        var rows = new List<string>();
        int failed = 0;
        void Verdict(string id, string what, bool ok, string detail)
        {
            verdicts.Add(new JsonObject { ["id"] = id, ["check"] = what, ["ok"] = ok, ["detail"] = detail });
            rows.Add($"| {id} | {what} | {(ok ? "PASS" : "FAIL")} | {detail} |");
            if (!ok) failed++;
            Console.WriteLine($"  [front:verify] {(ok ? "PASS" : "FAIL")} {id} {what}: {detail}");
        }

        for (int i = 0; i < edits.Count; i++)
        {
            var rule = edits[i]!.AsObject();
            string id = rule["id"]!.GetValue<string>();
            string sel = rule["selector"]!.GetValue<string>();
            string prop = rule["prop"]!.GetValue<string>();
            string op = rule["op"]!.GetValue<string>();
            double value = rule["value"]!.GetValue<double>();
            double tol = rule["tol"]?.GetValue<double>() ?? 0.5;
            var b = chain[i].Snap;
            var a = chain[i + 1].Snap;

            double bv = Num(b["styles"]?[sel]?[prop]);
            double av = Num(a["styles"]?[sel]?[prop]);
            bool ok = !double.IsNaN(bv) && !double.IsNaN(av) && op switch
            {
                "delta" => Math.Abs(av - bv - value) <= tol,
                "eq" => Math.Abs(av - value) <= tol,
                "gte" => av >= value,
                "lte" => av <= value,
                _ => throw new Exception("op desconhecida no edits.json: " + op),
            };
            Verdict(id, $"{sel} {prop} {op} {value}", ok, $"antes={bv} depois={av} (tol {tol})");

            foreach (var nr in rule["no_regress"]?.AsArray() ?? new JsonArray())
            {
                string[] parts = (nr?.GetValue<string>() ?? "").Split(':');
                if (parts.Length != 2) continue;
                double n0 = Num(b["styles"]?[parts[0]]?[parts[1]]);
                double n1 = Num(a["styles"]?[parts[0]]?[parts[1]]);
                Verdict(id, $"no-regressao {parts[0]} {parts[1]}", Math.Abs(n1 - n0) <= 0.5, $"antes={n0} depois={n1}");
            }
            Verdict(id, "integridade da pagina", (a["console_errors"]?.AsArray().Count ?? 1) == 0, $"erros de console={a["console_errors"]?.AsArray().Count ?? -1}");
        }

        // Metricas t/s dos dois modelos (gen 12B + edit E2B) no relatorio.
        var metrics = new JsonArray();
        foreach (var label in new[] { Path.Combine(Ws, "gen", "transcript.json") }
            .Concat(edits.Select(e => Path.Combine(Ws, "edit", e!["id"]!.GetValue<string>(), "transcript.json"))))
        {
            if (!File.Exists(label)) continue;
            var t = JsonNode.Parse(File.ReadAllText(label))!.AsObject();
            metrics.Add(new JsonObject
            {
                ["label"] = t["label"]?.GetValue<string>() ?? Path.GetFileName(Path.GetDirectoryName(label)!),
                ["model"] = t["model"]?.GetValue<string>() ?? (t["agent"]?.GetValue<string>() ?? "?"),
                ["turns"] = t["turns"]?.GetValue<int>() ?? 0,
                ["out_tokens"] = t["out_tokens"]?.GetValue<int>() ?? 0,
                ["model_seconds"] = t["model_seconds"]?.GetValue<double>() ?? 0,
                ["tps"] = t["tps"]?.GetValue<double>() ?? 0,
            });
        }

        var report = new JsonObject { ["when"] = DateTime.UtcNow.ToString("o"), ["ok"] = failed == 0, ["verdicts"] = verdicts, ["metrics"] = metrics };
        File.WriteAllText(Path.Combine(reports, "verdicts.json"), report.ToJsonString(Indented));

        var md = new StringBuilder();
        md.AppendLine("# Frontpipe - relatorio (12B gera, E2B ajusta, Playwright confere)");
        md.AppendLine($"\nGerado: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | resultado: **{(failed == 0 ? "PASS" : $"FAIL ({failed})")}**\n");
        md.AppendLine("| passo | verificacao | resultado | detalhe |");
        md.AppendLine("|---|---|---|---|");
        foreach (var r in rows) md.AppendLine(r);
        md.AppendLine("\n## Metricas t/s (por fase)\n");
        md.AppendLine("| fase | modelo | turnos | out_tokens | segundos | tok/s |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var m in metrics)
            md.AppendLine($"| {m!["label"]} | {m["model"]} | {m["turns"]} | {m["out_tokens"]} | {m["model_seconds"]} | {m["tps"]} |");
        File.WriteAllText(Path.Combine(reports, "report.md"), md.ToString());

        StopServer();
        TestContext.Check(failed == 0, $"frontpipe: {failed} verificacao(oes) falharam (ver {reports}/verdicts.json)");
        Console.WriteLine($"  [front:verify] relatorio: {Path.Combine(reports, "report.md")}");
        return Task.CompletedTask;
    }
}
