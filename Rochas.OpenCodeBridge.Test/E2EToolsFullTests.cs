using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Teste de tools COMPLETO: read, write, edit(sed), glob, grep, navegacao em
// pastas, git (init/add/commit/log), compilacao C# (dotnet new/build/run),
// validacao e execucao de script. Roda nos 2 modelos (GPU1 12B e GPU2 E2B)
// em paralelo via bridge pura. Deve passar antes da bateria belts.
internal static class E2EToolsFullTests
{
    static async Task RunOnBridge(string bridge, string label)
    {
        async Task<JsonObject> Exec(string name, JsonObject args)
        {
            return await TestContext.PostObj(bridge + "/api/tools/execute",
                new JsonObject { ["name"] = name, ["arguments"] = args });
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

        // 8. git read-only (status + log) — escrita git validada em DevPipeInfra
        var gst = await Exec("shell", new JsonObject { ["command"] = "git status --short" });
        TestContext.Check(gst["ok"]?.GetValue<bool>() == true, $"{label} git status ok");
        var glog = await Exec("shell", new JsonObject { ["command"] = "git log --oneline" });
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

    public static async Task FullTools()
    {
        await Task.WhenAll(
            RunOnBridge("http://127.0.0.1:4124", "GPU1-12B"),
            RunOnBridge("http://127.0.0.1:4125", "GPU2-E2B"));
        Console.WriteLine("[toolsfull] 2/2 modelos PASS");
    }
}
