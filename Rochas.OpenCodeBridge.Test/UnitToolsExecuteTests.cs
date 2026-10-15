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

        var denied = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "shell", ["arguments"] = new JsonObject { ["command"] = "rm -rf /tmp/x" } });
        TestContext.Check(denied["ok"]?.GetValue<bool>() == false, "tools shell rm negado");

        var unknown = await TestContext.PostObj(TestContext.Bridge + "/api/tools/execute",
            new JsonObject { ["name"] = "naoexiste", ["arguments"] = new JsonObject() });
        TestContext.Check(unknown["ok"]?.GetValue<bool>() == false, "tools desconhecida nega");
    }
}
