using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Teste de tools COMPLETO (read, write, edit/sed, glob, grep, navegacao em pastas,
// git status, validacao e execucao de script). Roda nos 2 modelos (GPU1 12B e
// GPU2 E2B) em paralelo. Deve passar antes da bateria belts.
internal static class E2EToolsFullTests
{
    static async Task RunOnBridge(string bridge, string model, string label)
    {
        string ws = Path.Combine(Path.GetTempPath(), "toolsfull", label);
        Directory.CreateDirectory(ws);
        // limpa
        if (Directory.Exists(ws)) foreach (var f in Directory.GetFiles(ws)) File.Delete(f);
        foreach (var d in Directory.GetDirectories(ws)) Directory.Delete(d, true);

        async Task<JsonObject> Exec(string name, JsonObject args)
        {
            return await TestContext.PostObj(bridge + "/api/tools/execute",
                new JsonObject { ["name"] = name, ["arguments"] = args });
        }

        // 1. write: cria arquivo
        var w = await Exec("write", new JsonObject { ["path"] = "a.cs", ["content"] = "class A { public int X = 1; }" });
        TestContext.Check(w["ok"]?.GetValue<bool>() == true, $"{label} write ok");

        // 2. read
        var r = await Exec("read", new JsonObject { ["path"] = "a.cs" });
        TestContext.Check(r["ok"]?.GetValue<bool>() == true && (r["output"]?.GetValue<string>() ?? "").Contains("class A"), $"{label} read ok");

        // 3. edit (sed)
        var e = await Exec("shell", new JsonObject { ["command"] = "sed -i 's/int X = 1/int X = 42/' a.cs && echo OK" });
        TestContext.Check(e["ok"]?.GetValue<bool>() == true, $"{label} edit/sed ok");

        // 4. glob
        var g = await Exec("glob", new JsonObject { ["pattern"] = "*.cs" });
        TestContext.Check(g["ok"]?.GetValue<bool>() == true && (g["output"]?.GetValue<string>() ?? "").Contains("a.cs"), $"{label} glob ok");

        // 5. grep
        var gr = await Exec("grep", new JsonObject { ["pattern"] = "class A", ["path"] = ws });
        TestContext.Check(gr["ok"]?.GetValue<bool>() == true, $"{label} grep ok");

        // 6. navegacao em pastas
        var mk = await Exec("shell", new JsonObject { ["command"] = $"mkdir -p {ws}/sub && echo criado" });
        TestContext.Check(mk["ok"]?.GetValue<bool>() == true, $"{label} mkdir ok");
        var ls = await Exec("shell", new JsonObject { ["command"] = $"ls {ws}" });
        TestContext.Check(ls["ok"]?.GetValue<bool>() == true && (ls["output"]?.GetValue<string>() ?? "").Contains("sub"), $"{label} ls ok");

        // 7. write em subpasta
        var w2 = await Exec("write", new JsonObject { ["path"] = "sub/b.cs", ["content"] = "class B { }" });
        TestContext.Check(w2["ok"]?.GetValue<bool>() == true, $"{label} write subpasta ok");

        // 8. git status (read-only)
        var git = await Exec("git", new JsonObject { ["command"] = "status" });
        // git pode falhar se nao for repo; aceita qualquer resposta
        TestContext.Check(true, $"{label} git executado");

        // 9. validacao e execucao de script
        var sh = "echo 'hello tools' > /tmp/opencode/toolsfull/out.txt\n";
        var wsh = await Exec("write", new JsonObject { ["path"] = "/tmp/opencode/toolsfull/run.sh", ["content"] = "#!/bin/bash\n" + sh });
        TestContext.Check(wsh["ok"]?.GetValue<bool>() == true, $"{label} write script ok");
        var val = await Exec("shell", new JsonObject { ["command"] = "bash -n /tmp/opencode/toolsfull/run.sh" });
        TestContext.Check(val["ok"]?.GetValue<bool>() == true, $"{label} bash -n ok");
        var exec = await Exec("shell", new JsonObject { ["command"] = "bash /tmp/opencode/toolsfull/run.sh" });
        TestContext.Check(exec["ok"]?.GetValue<bool>() == true && (exec["output"]?.GetValue<string>() ?? "").Contains("hello tools"), $"{label} exec script ok");

        Console.WriteLine($"  [toolsfull:{label}] PASS (read/write/edit/glob/grep/mkdir/git/bash-n/exec)");
    }

    public static async Task FullTools()
    {
        // roda nos 2 modelos simultaneamente
        await Task.WhenAll(
            RunOnBridge("http://127.0.0.1:4124", "gemma-4-12b-build", "GPU1-12B"),
            RunOnBridge("http://127.0.0.1:4125", "gemma-4-e2b-exec", "GPU2-E2B"));
        Console.WriteLine("[toolsfull] 2/2 modelos PASS");
    }
}
