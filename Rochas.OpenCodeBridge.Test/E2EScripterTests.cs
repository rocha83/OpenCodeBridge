using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// E2E Scripter (12B) -> Exec (E2B):
//   12B (role scripter) COMPÕE um script bash que executa a tarefa e o SALVA
//   (write_file) com um companion .md descrevendo o que o script faz (para o Exec ler).
//   E2B (role exec) LÊ o .md + o script e valida a sintaxe (bash -n) — execução
//   fica para outra fase (backlog). Nao executa o script.
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
        if (!model.EndsWith("-scripter")) model = model.Replace("-build", "-scripter").Replace("-plan", "-scripter");
        if (!model.Contains("scripter")) model += "-scripter";
        Console.WriteLine($"[scripter:gen] modelo {model} (bridge {TestContext.Bridge})");
        string ws = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opencode", "scripter");
        System.IO.Directory.CreateDirectory(ws);
        string scriptPath = System.IO.Path.Combine(ws, "tarefa.sh");
        string metaPath = System.IO.Path.Combine(ws, "tarefa.md");

        var tools = new JsonArray
        {
            new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="write_file", ["description"]="escreve arquivo",
                ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["content"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","content") } } }
        };
        var history = new JsonArray(
            new JsonObject { ["role"]="system", ["content"]="Voce e o Scripter (role scripter). Componha um script bash que cria um arquivo /tmp/scratchpad/ok.txt com o texto OK. Salve o script em tarefa.sh e um companion tarefa.md descrevendo o que o script faz (para o Exec ler antes de executar). Use write_file. Ao terminar responda apenas SCRIPTER-OK." },
            new JsonObject { ["role"]="user", ["content"]="Componha o script bash e o companion .md." });
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
                string name = c?["function"]?["name"]?.GetValue<string>() ?? "";
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
        TestContext.Check(System.IO.File.Exists(metaPath), "scripter escreveu tarefa.md");
        string script = System.IO.File.ReadAllText(scriptPath);
        TestContext.Check(script.Contains("#!"), "script tem shebang");
        // sintaxe
        var (code, outp) = Run("bash", ws, 60, "-n", "tarefa.sh");
        TestContext.Check(code == 0, "bash -n OK: " + outp);
        Console.WriteLine($"  [scripter:gen] OK (script {script.Length} bytes, meta {new System.IO.FileInfo(metaPath).Length} bytes)");
    }

    public static async Task ExecValidate()
    {
        string model = "gemma-4-e2b-exec";
        try
        {
            var st = await TestContext.GetObj(CpuBridge + "/api/status");
            string m = st["model"]?.GetValue<string>() ?? "gemma-4-e2b";
            model = m.EndsWith("-exec") ? m : m + "-exec";
        }
        catch { }
        Console.WriteLine($"[exec:validate] modelo {model} (bridge {CpuBridge})");
        string ws = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opencode", "scripter");
        TestContext.Check(System.IO.File.Exists(System.IO.Path.Combine(ws, "tarefa.sh")), "rode E-scripter-gen antes");
        TestContext.Check(System.IO.File.Exists(System.IO.Path.Combine(ws, "tarefa.md")), "companion .md ausente");

        var tools = new JsonArray
        {
            new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="read_file", ["description"]="le arquivo",
                ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path") } } }
        };
        var history = new JsonArray(
            new JsonObject { ["role"]="system", ["content"]="Voce e o Exec (role exec). Leia o companion .md e o script. Valide a sintaxe mentalmente e responda apenas EXEC-VALIDATE-OK. Nao execute o script (execucao fica para outra fase)." },
            new JsonObject { ["role"]="user", ["content"]="Leia tarefa.md e tarefa.sh e confirme que estao consistentes." });
        // exec reads via local tool (bridge exec has read). We'll just read locally and feed content.
        string md = System.IO.File.ReadAllText(System.IO.Path.Combine(ws, "tarefa.md"));
        string sh = System.IO.File.ReadAllText(System.IO.Path.Combine(ws, "tarefa.sh"));
        history.Add(new JsonObject { ["role"]="user", ["content"]="CONTEUDO tarefa.md:\n" + md + "\n\nCONTEUDO tarefa.sh:\n" + sh });
        var r = await TestContext.PostObj(CpuBridge + "/v1/chat/completions", new JsonObject { ["model"]=model, ["temperature"]=0.3, ["stream"]=false, ["messages"]=history });
        string content = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        Console.WriteLine($"  [exec:validate] {content}");
        // syntax check (independent, not by model)
        var (code, outp) = Run("bash", ws, 60, "-n", "tarefa.sh");
        TestContext.Check(code == 0, "bash -n OK: " + outp);
        Console.WriteLine("  [exec:validate] sintaxe OK");
    }
}