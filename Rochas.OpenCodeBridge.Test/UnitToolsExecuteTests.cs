using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Unit do executor na bridge: POST /api/tools/execute sem modelo.
// Auto-contido: escreve a sonda via write e relê via read (vale qualquer --workspace).
internal static class UnitToolsExecuteTests
{
    public static async Task ToolsExecute()
    {
        string probe = $"texec-{Guid.NewGuid():N}.txt";
        var written = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "write", ["arguments"] = new JsonObject { ["path"] = probe, ["content"] = "ola-bridge" } });
        TestContext.Check(written["ok"]?.GetValue<bool>() == true, "tools write ok");

        var read = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "read", ["arguments"] = new JsonObject { ["path"] = probe } });
        TestContext.Check(read["ok"]?.GetValue<bool>() == true && read["output"]?.GetValue<string>() == "ola-bridge", "tools read roundtrip");

        var echo = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "shell", ["arguments"] = new JsonObject { ["command"] = "echo oi-bridge" } });
        TestContext.Check(echo["ok"]?.GetValue<bool>() == true && (echo["output"]?.GetValue<string>() ?? "").Contains("oi-bridge"), "tools shell echo");

        // `>` precisa ter semantica de shell tambem nos comandos que o handler
        // roda por argv (antes virava argumento literal e o arquivo nao existia).
        string redir = $"redir-{Guid.NewGuid():N}.txt";
        var redirect = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "shell", ["arguments"] = new JsonObject { ["command"] = $"echo ola-redirect > {redir}" } });
        var redirRead = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "read", ["arguments"] = new JsonObject { ["path"] = redir } });
        TestContext.Check(redirect["ok"]?.GetValue<bool>() == true
            && (redirRead["output"]?.GetValue<string>() ?? "").Contains("ola-redirect"), "tools shell redirect > arquivo");

        var denied = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "shell", ["arguments"] = new JsonObject { ["command"] = "rm -rf /tmp/x" } });
        TestContext.Check(denied["ok"]?.GetValue<bool>() == false, "tools shell rm negado");

        var unknown = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "naoexiste", ["arguments"] = new JsonObject() });
        TestContext.Check(unknown["ok"]?.GetValue<bool>() == false, "tools desconhecida nega");
    }

    // Prova da tool git no /api/tools/execute: config/-C com role build e
    // negativa no plan. Sem isso o ciclo git do dual nao tem gate unitario.
    public static async Task GitToolCycle()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opencode", "git-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        async Task<JsonObject> Call(string command, string role = "build")
            => await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute", new JsonObject
            {
                ["name"] = "git",
                ["role"] = role,
                ["arguments"] = new JsonObject { ["command"] = command, ["path"] = dir }
            });

        var init = await Call("init");
        TestContext.Check(init["ok"]?.GetValue<bool>() == true, "U-git init");
        var email = await Call("config user.email teste@exemplo.local");
        TestContext.Check(email["ok"]?.GetValue<bool>() == true, "U-git config email");
        var name = await Call("config user.name Teste");
        TestContext.Check(name["ok"]?.GetValue<bool>() == true, "U-git config name");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        var add = await Call("add -A");
        TestContext.Check(add["ok"]?.GetValue<bool>() == true, "U-git add");
        var commit = await Call("commit -m inicial");
        TestContext.Check(commit["ok"]?.GetValue<bool>() == true, "U-git commit");
        var log = await Call("log --oneline");
        TestContext.Check(log["ok"]?.GetValue<bool>() == true
            && (log["output"]?.GetValue<string>() ?? "").Contains("inicial"), "U-git log");
        var denied = await Call("config user.email x", "plan");
        TestContext.Check(denied["ok"]?.GetValue<bool>() == false, "U-git config negado no plan");
    }
}
