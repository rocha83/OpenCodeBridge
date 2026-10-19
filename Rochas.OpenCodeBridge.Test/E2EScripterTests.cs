using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// E2E Scripter (12B compoe + valida sintaxe) -> Scripter E2B (gera companion .md)
// -> Exec (futuro le companion .md e executa os scripts):
//   1) 12B (role scripter) COMPÕE um script bash que executa a tarefa e o SALVA
//      (write_file); valida sintaxe com bash -n.
//   2) E2B (role scripter) LÊ o script e gera o companion .md descrevendo o que
//      o script faz (para o Exec ler antes de executar).
//   3) Exec (role exec) — backlog: le o companion .md e EXECUTA os scripts.
internal static class E2EScripterTests
{
    const string CpuBridge = "http://127.0.0.1:4125";

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

    public static async Task ScripterGen()
    {
        string model = TestContext.SuiteModel;
        if (!model.Contains("scripter")) model = model.Replace("-build", "-scripter").Replace("-plan", "-scripter");
        if (!model.Contains("scripter")) model += "-scripter";
        Console.WriteLine($"[scripter:gen] modelo {model} (bridge {TestContext.Bridge})");
        string ws = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opencode", "scripter");
        System.IO.Directory.CreateDirectory(ws);
        string scriptPath = System.IO.Path.Combine(ws, "tarefa.sh");

        var tools = new JsonArray
        {
            new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="write_file", ["description"]="escreve arquivo",
                ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["content"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","content") } } }
        };
        var history = new JsonArray(
            new JsonObject { ["role"]="system", ["content"]="Voce e o Scripter (role scripter, 12B). Componha um script bash que cria um arquivo /tmp/scratchpad/ok.txt com o texto OK e imprime OK. Salve o script em tarefa.sh com shebang #!/usr/bin/env bash. Use write_file. Ao terminar responda apenas SCRIPTER-OK." },
            new JsonObject { ["role"]="user", ["content"]="Componha o script bash." });
        for (int t = 0; t < 8; t++)
        {
            var r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions", new JsonObject { ["model"]=model, ["temperature"]=0.3, ["stream"]=false, ["messages"]=history.DeepClone(), ["tools"]=tools.DeepClone() });
            var msg = r["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message");
            var calls = msg["tool_calls"]?.AsArray();
            if (calls is null || calls.Count == 0) { Console.WriteLine($"  [scripter] final: {msg["content"]}"); break; }
            var echo = new JsonArray();
            foreach (var c in calls) echo.Add(new JsonObject { ["id"]=c?["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=c?["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=c?["function"]?["arguments"]?.GetValue<string>()??"{}" } });
            history.Add(new JsonObject { ["role"]="assistant", ["content"]=msg["content"]?.GetValue<string>()??"", ["tool_calls"]=echo });
            foreach (var c in calls)
            {
                var a = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject();
                string path = a["path"]?.GetValue<string>() ?? "";
                string content = a["content"]?.GetValue<string>() ?? "";
                string full = System.IO.Path.Combine(ws, path);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                System.IO.File.WriteAllText(full, content);
                history.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=c?["id"]?.GetValue<string>()??"", ["content"]=$"escrito {path}" });
                Console.WriteLine($"  [scripter] wrote {path} ({content.Length} bytes)");
            }
        }
        TestContext.Check(System.IO.File.Exists(scriptPath), "scripter escreveu tarefa.sh");
        string script = System.IO.File.ReadAllText(scriptPath);
        TestContext.Check(script.Contains("#!"), "script tem shebang");
        var (code, outp) = Run("bash", ws, 60, "-n", "tarefa.sh");
        TestContext.Check(code == 0, "bash -n OK: " + outp);
        Console.WriteLine($"  [scripter:gen] OK (script {script.Length} bytes)");
    }

    public static async Task CompanionGen()
    {
        // E2B (role scripter) le o script e gera o companion .md.
        string model = "gemma-4-e2b-scripter";
        try
        {
            var st = await TestContext.GetObj(CpuBridge + "/api/status");
            string m = st["model"]?.GetValue<string>() ?? "gemma-4-e2b";
            model = m.EndsWith("-scripter") ? m : m + "-scripter";
        }
        catch { }
        Console.WriteLine($"[companion:gen] modelo {model} (bridge {CpuBridge})");
        string ws = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opencode", "scripter");
        TestContext.Check(System.IO.File.Exists(System.IO.Path.Combine(ws, "tarefa.sh")), "rode E-scripter-gen antes");
        string script = System.IO.File.ReadAllText(System.IO.Path.Combine(ws, "tarefa.sh"));
        string metaPath = System.IO.Path.Combine(ws, "tarefa.md");

        var tools = new JsonArray
        {
            new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="write_file", ["description"]="escreve arquivo",
                ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["content"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","content") } } }
        };
        var history = new JsonArray(
            new JsonObject { ["role"]="system", ["content"]="Voce e o Scripter (role scripter, E2B). Leia o script bash fornecido e gere um companion .md (tarefa.md) descrevendo o que o script faz, seus passos e como o Exec deve executa-lo. Salve com write_file em tarefa.md. Ao terminar responda apenas COMPANION-OK." },
            new JsonObject { ["role"]="user", ["content"]="SCRIPT:\n" + script + "\n\nGere o companion tarefa.md." });
        for (int t = 0; t < 8; t++)
        {
            var r = await TestContext.PostObj(CpuBridge + "/v1/chat/completions", new JsonObject { ["model"]=model, ["temperature"]=0.3, ["stream"]=false, ["messages"]=history.DeepClone(), ["tools"]=tools.DeepClone() });
            var msg = r["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message");
            var calls = msg["tool_calls"]?.AsArray();
            if (calls is null || calls.Count == 0) { Console.WriteLine($"  [companion] final: {msg["content"]}"); break; }
            var echo = new JsonArray();
            foreach (var c in calls) echo.Add(new JsonObject { ["id"]=c?["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=c?["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=c?["function"]?["arguments"]?.GetValue<string>()??"{}" } });
            history.Add(new JsonObject { ["role"]="assistant", ["content"]=msg["content"]?.GetValue<string>()??"", ["tool_calls"]=echo });
            foreach (var c in calls)
            {
                var a = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject();
                string path = a["path"]?.GetValue<string>() ?? "";
                string content = a["content"]?.GetValue<string>() ?? "";
                string full = System.IO.Path.Combine(ws, path);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                System.IO.File.WriteAllText(full, content);
                history.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=c?["id"]?.GetValue<string>()??"", ["content"]=$"escrito {path}" });
                Console.WriteLine($"  [companion] wrote {path} ({content.Length} bytes)");
            }
        }
        TestContext.Check(System.IO.File.Exists(metaPath), "companion gerou tarefa.md");
        Console.WriteLine($"  [companion:gen] OK (meta {new System.IO.FileInfo(metaPath).Length} bytes)");
    }

    public static Task ExecValidate()
    {
        // Backlog: Exec le o companion .md e executa os scripts. Por ora, apenas
        // valida que companion existe e imprime seu conteudo (leitura pre-execucao).
        string ws = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opencode", "scripter");
        TestContext.Check(System.IO.File.Exists(System.IO.Path.Combine(ws, "tarefa.sh")), "rode E-scripter-gen antes");
        TestContext.Check(System.IO.File.Exists(System.IO.Path.Combine(ws, "tarefa.md")), "rode E-companion-gen antes");
        string md = System.IO.File.ReadAllText(System.IO.Path.Combine(ws, "tarefa.md"));
        string sh = System.IO.File.ReadAllText(System.IO.Path.Combine(ws, "tarefa.sh"));
        Console.WriteLine("  [exec:read] --- companion .md ---");
        Console.WriteLine(md);
        Console.WriteLine("  [exec:read] --- script ---");
        Console.WriteLine(sh);
        var (code, outp) = Run("bash", ws, 60, "-n", "tarefa.sh");
        TestContext.Check(code == 0, "bash -n OK: " + outp);
        Console.WriteLine("  [exec:read] sintaxe OK (execucao fica para outra fase/backlog)");
        return Task.CompletedTask;
    }
}