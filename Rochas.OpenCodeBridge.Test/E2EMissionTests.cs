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
        using var p = Process.Start(psi) ?? throw new Exception("sem processo");
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout " + exe + " " + args); }
        return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    }

    static async Task<JsonObject> Chat(string bridge, string model, JsonArray messages, JsonArray? tools)
    {
        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["max_tokens"] = 1500 };
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
        arr.Add(P("git", new JsonObject { ["action"] = new JsonObject { ["type"] = "string", ["description"] = "init|add|commit|status|log" }, ["message"] = new JsonObject { ["type"] = "string" }, ["paths"] = new JsonObject { ["type"] = "string" } }, "action"));
        arr.Add(P("zip", new JsonObject { ["output"] = new JsonObject { ["type"] = "string" }, ["inputs"] = new JsonObject { ["type"] = "string", ["description"] = "espaco-separado, relativo ao projeto" } }, "output", "inputs"));
        return arr;
    }

    public static async Task MissionTools()
    {
        string bridge = TestContext.Bridge;
        string model = TestContext.ServedModel.Length > 0 ? TestContext.ServedModel : "qwen3.5-9b";
        if (!model.EndsWith("-build")) model += "-build";
        string ws = Path.Combine(Path.GetTempPath(), "e2e-mission");
        if (Directory.Exists(ws)) Directory.Delete(ws, true);
        Directory.CreateDirectory(ws);

        var sys = new JsonObject { ["role"] = "system", ["content"] = "Voce e engenheiro senior C# e F#. Responda SOMENTE com tool calls (uma por vez), sem prosa." };
        var history = new JsonArray(sys);
        history.Add(new JsonObject { ["role"] = "user", ["content"] = "Use tools: crie projeto C# (dotnet new console -n mcs) e grave Program.cs com Simpson de sin[0,pi] e media/desvio de {2,4,4,4,5,5,7,9}. Imprima os 3 numeros com 6 casas." });

        string code = "", final = "";
        for (int turn = 0; turn < 14; turn++)
        {
            var r = await Chat(bridge, model, (JsonArray)history.DeepClone(), Tools());
            var msg = r["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message");
            var calls = msg["tool_calls"]?.AsArray();
            if (calls is null || calls.Count == 0)
            {
                final = msg["content"]?.GetValue<string>() ?? "";
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
                if (fn == "write" && (args["path"]?.GetValue<string>() ?? "").EndsWith("Program.cs")) code = args["content"]?.GetValue<string>() ?? code;
                echo.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = tc?["id"]?.GetValue<string>() ?? "", ["content"] = res });
            }
            var freshCalls = new JsonArray();
            foreach (var c in calls)
                freshCalls.Add(new JsonObject { ["id"] = c?["id"]?.GetValue<string>() ?? "", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}" } });
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = msg["content"]?.GetValue<string>() ?? "", ["tool_calls"] = freshCalls });
            foreach (var e in echo) history.Add((JsonObject)e.DeepClone());
        }
        TestContext.Check(code.Contains("Sin") || code.Contains("sin"), "Program.cs com Simpson escrito");
        string runOut = Run("dotnet", "run --project mcs -c Release", ws, 240);
        TestContext.Check(runOut.Contains("2,000000") || runOut.Contains("2.000000"), "Simpson = 2.0, saiu: " + runOut[..Math.Min(120, runOut.Length)]);
        TestContext.Check(runOut.Contains("5,000000") || runOut.Contains("5.000000"), "media = 5.0");
        Console.WriteLine($"  [mission] C# ok:\n{runOut.Trim()}");

        // Fase 2: versionamento + pacote com as tools nativas git/zip.
        history.Add(new JsonObject { ["role"] = "user", ["content"] = "Use tools: versione com a tool git (init, add, commit com mensagem) e empacote com a tool zip (output entrega.zip, inputs mcs). So tool calls." });
        for (int turn = 0; turn < 8; turn++)
        {
            var r2 = await Chat(bridge, model, (JsonArray)history.DeepClone(), Tools());
            var m2 = r2["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (git/zip)");
            var c2 = m2["tool_calls"]?.AsArray();
            if (c2 is null || c2.Count == 0) break;
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
                string res2 = Exec(ws, c?["function"]?["name"]?.GetValue<string>() ?? "", a2);
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
        if (!p.WaitForExit(180000)) { try { p.Kill(true); } catch { } return "TIMEOUT."; }
        return $"exit={p.ExitCode}\n" + (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd())[^4000..];
    }

    static string Exec(string ws, string fn, JsonObject args)
    {
        try
        {
            if (fn == "shell")
            {
                string cmd = args["command"]?.GetValue<string>() ?? "";
                if (cmd.Contains("rm -rf /") || cmd.Contains("sudo")) return "NEGADO.";
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
