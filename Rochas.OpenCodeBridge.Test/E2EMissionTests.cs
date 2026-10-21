// Missao tools E2E (C# + F# + git + zip) na suite: modelo constroi, suite executa e confere numeros.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

internal static class E2EMissionTests
{
    static string Run(string exe, string args, string cwd, int secs = 240)
    {
        var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";   // net8/net9 apphost roda no runtime instalado
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi) ?? throw new Exception("sem processo");
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout " + exe + " " + args); }
        return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    }

    static async Task<JsonObject> Chat(string bridge, string model, JsonArray messages, JsonArray? tools)
    {
        var body = new JsonObject { ["model"] = model, ["messages"] = messages };
        if (tools is not null) body["tools"] = tools;
        return await TestContext.PostObj(bridge + "/v1/chat/completions", body);
    }

    static JsonArray Tools()
    {
        JsonObject P(string desc, JsonObject props, params string[] req)
        {
            var r = new JsonArray();
            foreach (var x in req) r.Add(x);
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = desc,
                    ["description"] = desc,
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = r },
                },
            };
        }
        var arr = new JsonArray();
        arr.Add(P("shell", new JsonObject { ["command"] = new JsonObject { ["type"] = "string" } }, "command"));
        arr.Add(P("write", new JsonObject { ["path"] = new JsonObject { ["type"] = "string" }, ["content"] = new JsonObject { ["type"] = "string" } }, "path", "content"));
        arr.Add(P("git", new JsonObject { ["action"] = new JsonObject { ["type"] = "string", ["description"] = "init|add|commit|status|log" }, ["message"] = new JsonObject { ["type"] = "string", ["description"] = "mensagem do commit (obrigatoria no commit)" }, ["paths"] = new JsonObject { ["type"] = "string", ["description"] = "caminhos a adicionar (vazio = tudo)" } }, "action"));
        arr.Add(P("zip", new JsonObject { ["output"] = new JsonObject { ["type"] = "string", ["description"] = "nome do arquivo .zip de saida" }, ["inputs"] = new JsonObject { ["type"] = "string", ["description"] = "caminhos relativos ao workspace, separados por espaco (bin/obj excluidos)" } }, "output", "inputs"));
        return arr;
    }

    public static async Task MissionTools()
    {
        string bridge = TestContext.Bridge;
        string model = TestContext.SuiteModel;
        if (!model.EndsWith("-build")) model += "-build";
        string ws = Path.Combine(Path.GetTempPath(), "e2e-mission");
        if (Directory.Exists(ws)) Directory.Delete(ws, true);
        Directory.CreateDirectory(ws);
        // Scaffold dos projetos e' do harness (template `dotnet new`, estilo Visual Studio):
        // o modelo so escreve o fonte e roda; elimina a variabilidade do `dotnet new` no modelo.
        TestContext.ScaffoldProject("mcs", Path.Combine(ws, "mcs"));
        TestContext.ScaffoldProject("mfs", Path.Combine(ws, "mfs"), "F#");

        var sys = new JsonObject { ["role"] = "system", ["content"] = "Voce e engenheiro senior C# e F#. Responda SOMENTE com tool calls (uma por vez), sem prosa." };
        var history = new JsonArray(sys);
        history.Add(new JsonObject { ["role"] = "user", ["content"] = $"Use tools, uma por turno. (1) write no caminho ABSOLUTO {ws}/mcs/Program.cs (o projeto ja existe em {ws}/mcs; nao recrie, nao use echo/heredoc): programa que calcula Simpson de Math.Sin em [0,pi] (n=100 subintervalos) e media/desvio de {{2,4,4,4,5,5,7,9}}, imprimindo os 3 numeros com 6 casas. (2) rode o projeto e confira a saida; se falhar, leia o erro, corrija Program.cs e rode de novo ate imprimir 2.000000 e 5.000000." });

        string fcode = "", final = "";
        string logPath = Path.Combine(ws, "_mission.log");
        void LogT(string s) { try { File.AppendAllText(logPath, s + "\n"); } catch { } }
        for (int turn = 0; turn < 14; turn++)
        {
            var r = await Chat(bridge, model, (JsonArray)history.DeepClone(), Tools());
            var msg = r["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message");
            var calls = msg["tool_calls"]?.AsArray();
            if (calls is null || calls.Count == 0)
            {
                final = msg["content"]?.GetValue<string>() ?? "";
                LogT($"[C#] T{turn} SEM CALLS: {final[..Math.Min(200, final.Length)]}");
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = final });
                if (turn >= 2) break;
                history.Add(new JsonObject { ["role"] = "user", ["content"] = "Sem prosa: emita a proxima tool call agora." });
                continue;
            }
            var echo = new JsonArray();
            foreach (var tc in calls)
            {
                var fn = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                JsonObject args;
                try { args = JsonNode.Parse(tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject(); }
                catch { args = new JsonObject(); }
                string res = Exec(ws, fn, args);
                LogT($"[C#] T{turn} {fn} {args.ToJsonString()[..Math.Min(160, args.ToJsonString().Length)]} -> {res[..Math.Min(180, res.Length)]}");
                if (fn == "write" && (args["path"]?.GetValue<string>() ?? "").EndsWith("Program.fs")) fcode = args["content"]?.GetValue<string>() ?? fcode;
                echo.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = tc?["id"]?.GetValue<string>() ?? "", ["content"] = res });
            }
            var freshCalls = new JsonArray();
            foreach (var c in calls)
                freshCalls.Add(new JsonObject { ["id"] = c?["id"]?.GetValue<string>() ?? "", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}" } });
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = msg["content"]?.GetValue<string>() ?? "", ["tool_calls"] = freshCalls });
            foreach (var e in echo) history.Add((JsonObject)e!.DeepClone());
            // Sucesso atingido: para de editar (o modelo tende a quebrar o que ja funciona).
            if (echo.Any(e => { var s = e?["content"]?.GetValue<string>() ?? ""; return (s.Contains("2,000000") || s.Contains("2.000000")) && (s.Contains("5,000000") || s.Contains("5.000000")); })) break;
        }
        string csFile = Path.Combine(ws, "mcs", "Program.cs");
        string csSrc = File.Exists(csFile) ? File.ReadAllText(csFile) : "";
        TestContext.Check((csSrc.Contains("Sin") || csSrc.Contains("sin")) && csSrc.Length > 60,
            "mcs/Program.cs com Simpson (nao template), veio: " + csSrc[..Math.Min(100, csSrc.Length)]);
        string runOut = Run("dotnet", "run --project mcs -c Release", ws, 240);
        LogT($"[C#] RUN -> {runOut[..Math.Min(220, runOut.Length)]}");
        TestContext.Check(runOut.Contains("2,000000") || runOut.Contains("2.000000"), "Simpson = 2.0, saiu: " + runOut[..Math.Min(120, runOut.Length)]);
        TestContext.Check(runOut.Contains("5,000000") || runOut.Contains("5.000000"), "media = 5.0");
        Console.WriteLine($"  [mission] C# ok:\n{runOut.Trim()}");

        // Fase 2: portabilidade para F# (mesma matematica, System.Math qualificado).
        int historyBeforeFs = history.Count;
        string fsOut = "";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            if (attempt > 1)
            {
                // Flake do modelo (o F# as vezes sai com erro de sintaxe): refaz a fase limpa.
                while (history.Count > historyBeforeFs) history.RemoveAt(history.Count - 1);
                LogT($"[F#] retry {attempt}: F# anterior nao compilou/rodou");
            }
            history.Add(new JsonObject { ["role"] = "user", ["content"] = $"Use tools, uma por turno. (1) write no caminho ABSOLUTO {ws}/mfs/Program.fs (o projeto ja existe em {ws}/mfs; nao recrie o projeto). Escreva F# que imprima 3 numeros com 6 casas via printfn \"%.6f\" valor: (a) Simpson de sin em [0,pi] com n=100 -> 2.000000; (b) media de [2.0;4.0;4.0;4.0;5.0;5.0;7.0;9.0] -> 5.000000; (c) desvio padrao da mesma lista. OBRIGATORIO em F# (siga a risca): use acumulador mutavel e um for - `let mutable soma = 0.0` e `for i in 0 .. n do` com atribuicao `soma <- soma + ...`; NAO use Seq/List.map/List.fold/comprehension para a integral. Integral: h = pi / float n; para i de 1 a (n-1): soma <- soma + peso * sin(float i * h), com peso 4.0 se i for impar e 2.0 se par; resultado = h / 3.0 * soma. Desvio padrao: sqrt (List.average (List.map (fun v -> (v - media) ** 2.0) data)). (2) rode `dotnet run --project {ws}/mfs` e corrija ate imprimir 2.000000 e 5.000000." });
        for (int turn = 0; turn < 22; turn++)
        {
            var rf = await Chat(bridge, model, (JsonArray)history.DeepClone(), Tools());
            var mf = rf["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (F#)");
            var cf = mf["tool_calls"]?.AsArray();
            if (cf is null || cf.Count == 0)
            {
                LogT($"[F#] T{turn} SEM CALLS: {(mf["content"]?.GetValue<string>() ?? "")[..Math.Min(200, (mf["content"]?.GetValue<string>() ?? "").Length)]}");
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = mf["content"]?.GetValue<string>() ?? "" });
                if (turn >= 4) break;
                history.Add(new JsonObject { ["role"] = "user", ["content"] = "Sem prosa: leia o erro de build (se houver) e emita ja a proxima tool call." });
                continue;
            }
            var ff = new JsonArray();
            foreach (var c in cf)
                ff.Add(new JsonObject { ["id"] = c?["id"]?.GetValue<string>() ?? "", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}" } });
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = mf["content"]?.GetValue<string>() ?? "", ["tool_calls"] = ff });
            var fres = new List<string>();
            foreach (var c in cf)
            {
                JsonObject af;
                try { af = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject(); }
                catch { af = new JsonObject(); }
                string fnf = c?["function"]?["name"]?.GetValue<string>() ?? "";
                string resf = Exec(ws, fnf, af);
                LogT($"[F#] T{turn} {fnf} {af.ToJsonString()[..Math.Min(160, af.ToJsonString().Length)]} -> {resf[..Math.Min(180, resf.Length)]}");
                if (fnf == "write" && (af["path"]?.GetValue<string>() ?? "").EndsWith("Program.fs")) fcode = af["content"]?.GetValue<string>() ?? fcode;
                fres.Add(resf);
                history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = resf });
            }
            // Sucesso atingido: para de editar (o modelo tende a quebrar o F# que ja compila).
            if (fres.Any(s => (s.Contains("2,000000") || s.Contains("2.000000")) && (s.Contains("5,000000") || s.Contains("5.000000")))) break;
        }
            string fsFile = Path.Combine(ws, "mfs", "Program.fs");
            string fsSrc = File.Exists(fsFile) ? File.ReadAllText(fsFile) : "";
            fsOut = Run("dotnet", "run --project mfs -c Release", ws, 240);
            LogT($"[F#] RUN -> {fsOut[..Math.Min(220, fsOut.Length)]}");
            bool ok = fsSrc.Contains("Math") && fcode.Contains("Math")
                && (fsOut.Contains("2,000000") || fsOut.Contains("2.000000"))
                && (fsOut.Contains("5,000000") || fsOut.Contains("5.000000"));
            if (ok) break;
        }
        TestContext.Check(fsOut.Contains("2,000000") || fsOut.Contains("2.000000"), "F# Simpson = 2.0, saiu: " + fsOut[..Math.Min(160, fsOut.Length)]);
        TestContext.Check(fsOut.Contains("5,000000") || fsOut.Contains("5.000000"), "F# media = 5.0");
        Console.WriteLine($"  [mission] F# ok:\n{fsOut.Trim()}");

        // Fase 3: versionamento + pacote com as tools nativas git/zip.
        history.Add(new JsonObject { ["role"] = "user", ["content"] = "Conclua a entrega: versione o workspace no git (a entrega final deve estar commitada) e gere o pacote entrega.zip na raiz do workspace. So tool calls ate concluir, sem texto." });
        for (int turn = 0; turn < 12; turn++)
        {
            var r2 = await Chat(bridge, model, (JsonArray)history.DeepClone(), Tools());
            var m2 = r2["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (git/zip)");
            var c2 = m2["tool_calls"]?.AsArray();
            if (c2 is null || c2.Count == 0)
            {
                // Modelo encerrou sem tool call: se a entrega ja existe, ok; senao cutuca so o que falta.
                if (Directory.GetFiles(ws, "entrega.zip").Length == 1) break;
                LogT($"[git/zip] T{turn + 1} sem tool call e sem entrega.zip; pedindo a tool zip");
                history.Add(new JsonObject { ["role"] = "user",
                    ["content"] = "Voce encerrou sem concluir a entrega. Verifique o que falta e emita a tool call correspondente, sem texto." });
                continue;
            }
            var f2 = new JsonArray();
            foreach (var c in c2)
                f2.Add(new JsonObject { ["id"] = c?["id"]?.GetValue<string>() ?? "", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}" } });
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = m2["content"]?.GetValue<string>() ?? "", ["tool_calls"] = f2 });
            foreach (var c in c2)
            {
                JsonObject a2;
                try { a2 = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject(); }
                catch { a2 = new JsonObject(); }
                string fnName = c?["function"]?["name"]?.GetValue<string>() ?? "";
                string res2 = Exec(ws, fnName, a2);
                LogT($"[git/zip] T{turn + 1} {fnName} -> {res2.Split('\n')[0]}");
                history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = res2 });
            }
        }
        TestContext.Check(Directory.Exists(Path.Combine(ws, ".git")), "git init executado");
        TestContext.Check(File.Exists(Path.Combine(ws, ".git", "HEAD")), "repo git criado");
        TestContext.Check(Directory.GetFiles(ws, "entrega.zip").Length == 1, "entrega.zip gerado");
    }

    static string Shell(string ws, string cmd)
    {
        var psi = new ProcessStartInfo("/bin/bash", "-c \"" + cmd.Replace("\"", "\\\"") + "\"")
        { WorkingDirectory = ws, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var soTask = p.StandardOutput.ReadToEndAsync();
        var seTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(180000)) { try { p.Kill(true); } catch { } return "TIMEOUT."; }
        return $"exit={p.ExitCode}\n" + soTask.Result + seTask.Result;
    }

    static string Exec(string ws, string fn, JsonObject args)
    {
        try
        {
            if (fn == "shell")
            {
                string cmd = args["command"]?.GetValue<string>() ?? "";
                if (cmd.Contains("rm ") || cmd.Contains("sudo") || cmd.Contains("rm -")) return "NEGADO.";
                return Shell(ws, cmd);
            }
            if (fn == "git")
            {
                string action = (args["action"]?.GetValue<string>() ?? "").ToLowerInvariant();
                if (action is not ("init" or "add" or "commit" or "status" or "log")) return "NEGADO (acao git).";
                string cmd = action switch
                {
                    "init" => "git init -q && git config user.name e2e && git config user.email e2e@local",
                    "add" => "git add -A",
                    "commit" => "git commit -qm \"" + (args["message"]?.GetValue<string>() ?? "e2e").Replace("\"", "") + "\"",
                    "status" => "git status --short",
                    _ => "git log --oneline -3",
                };
                return Shell(ws, cmd);
            }
            if (fn == "zip")
            {
                string output = args["output"]?.GetValue<string>() ?? "entrega.zip";
                if (output.Contains("..") || Path.IsPathRooted(output)) return "NEGADO.";
                string inputs = args["inputs"]?.GetValue<string>() ?? "";
                if (inputs.Contains("..")) return "NEGADO.";
                return Shell(ws, "zip -qr " + output + " " + inputs + " -x '*/bin/*' -x '*/obj/*' && unzip -l " + output + " | head -n 12");
            }
            if (fn == "write")
            {
                string rel = args["path"]?.GetValue<string>() ?? "";
                string full = Path.GetFullPath(Path.Combine(ws, rel));
                if (!full.StartsWith(ws)) return "NEGADO.";
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ws);
                File.WriteAllText(full, args["content"]?.GetValue<string>() ?? "");
                return "gravado.";
            }
            return "TOOL DESCONHECIDA.";
        }
        catch (Exception ex) { return "ERRO: " + ex.Message; }
    }
}
