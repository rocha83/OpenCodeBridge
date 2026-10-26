using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Prova o contrato minimo dos histogramas usados pela sonda.</summary>
internal static class UnitCodingJourneyTests
{
    public static Task AnalyzesBothModes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opencode", "coding-journey-unit-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string opencodePath = Path.Combine(dir, "opencode.json");
        string bridgePath = Path.Combine(dir, "bridge.json");

        var opencode = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["id"] = "ses_unit",
                ["agent"] = "build",
                ["model"] = new JsonObject { ["id"] = "m", ["providerID"] = "p" },
                ["tokens"] = new JsonObject { ["input"] = 10, ["output"] = 5 }
            },
            ["messages"] = BuildOpencodeMessages()
        };
        File.WriteAllText(opencodePath, opencode.ToJsonString());

        var open = CodingJourney.AnalyzeOpencode(opencodePath);
        TestContext.Check(open["tools_total"]?.GetValue<int>() == 4, "opencode tools_total=4");
        TestContext.Check(open["tool_histogram"]?["edit"]?.GetValue<int>() == 1, "opencode edit=1");
        TestContext.Check(open["validation_histogram"]?["bash-n"]?.GetValue<int>() == 1, "opencode bash-n=1");
        TestContext.Check(open["sequence_histogram"]?["read>edit"]?.GetValue<int>() == 1, "opencode read>edit=1");
        TestContext.Check(open["metrics"]?["edit_ratio"]?.GetValue<double>() == 0.5, "opencode edit_ratio=0.5");
        TestContext.Check(open["metrics"]?["failure_rate"]?.GetValue<double>() == 0.25, "opencode failure_rate=0.25");
        TestContext.Check(CodingJourney.CallsFromOpencode(opencodePath).Count == 4, "opencode calls=4");

        var bridge = new JsonObject
        {
            ["mode"] = "bridge",
            ["model"] = "m",
            ["prompt"] = "faca",
            ["text"] = "ok",
            ["calls"] = new JsonArray(
                Call(0, "read", new JsonObject { ["path"] = "/tmp/a.cs" }, true),
                Call(0, "edit", new JsonObject { ["path"] = "/tmp/a.cs" }, true),
                Call(1, "shell", new JsonObject { ["command"] = "bash -n /tmp/a.cs" }, true),
                Call(1, "write", new JsonObject { ["path"] = "/tmp/b.cs" }, false))
        };
        File.WriteAllText(bridgePath, bridge.ToJsonString());

        var br = CodingJourney.AnalyzeBridge(bridgePath);
        TestContext.Check(br["tools_total"]?.GetValue<int>() == 4, "bridge tools_total=4");
        TestContext.Check(br["validation_histogram"]?["bash-n"]?.GetValue<int>() == 1, "bridge bash-n=1");
        TestContext.Check(br["turn_tools_histogram"]?["2"]?.GetValue<int>() == 2, "bridge turn 2 tools=2");
        TestContext.Check(br["sequence_histogram"]?["edit>shell"]?.GetValue<int>() == 1, "bridge edit>shell=1");
        TestContext.Check(br["metrics"]?["failure_rate"]?.GetValue<double>() == 0.25, "bridge failure_rate=0.25");

        var contrast = CodingJourney.Contrast(open, br);
        TestContext.Check(contrast["tool_delta"]?["edit"]?.GetValue<int>() == 0, "contrast edit delta=0");
        return Task.CompletedTask;
    }

    /// <summary>A sonda le o stream history e materializa o journey no probeDir.</summary>
    public static async Task StepProbeWritesJourney()
    {
        string dir = Path.Combine(Path.GetTempPath(), "opencode", "coding-probe-unit-" + Environment.ProcessId);
        string journey = Path.Combine(dir, "journey");
        Directory.CreateDirectory(journey);
        var opencode = new JsonObject
        {
            ["info"] = new JsonObject { ["id"] = "ses_probe", ["agent"] = "build" },
            ["messages"] = BuildOpencodeMessages()
        };
        File.WriteAllText(Path.Combine(journey, "probe-step.opencode.raw.json"), opencode.ToJsonString());

        using (StepProbe.Start(dir, "probe-step", "opencode", null, null, null, journey))
            await Task.Delay(400);

        string output = Path.Combine(dir, "probes", "probe-step.journey.json");
        TestContext.Check(File.Exists(output), "probe journey file criado");
        TestContext.Check(File.Exists(Path.Combine(journey, "probe-step.histograms.json")), "histograms no bundle");
        var hist = JsonNode.Parse(File.ReadAllText(output))?.AsObject() ?? new JsonObject();
        TestContext.Check(hist["opencode"]?["tools_total"]?.GetValue<int>() == 4, "probe journey opencode tools=4");
    }

    private static JsonObject Call(int round, string name, JsonObject args, bool ok) => new()
    {
        ["round"] = round,
        ["name"] = name,
        ["args"] = args,
        ["ok"] = ok,
        ["output"] = ok ? "ok" : "erro"
    };

    private static JsonArray BuildOpencodeMessages()
    {
        var messages = new JsonArray();
        messages.Add(new JsonObject { ["type"] = "user", ["text"] = "faca" });
        messages.Add(new JsonObject
        {
            ["type"] = "assistant",
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "reasoning", ["text"] = "penso" },
                Tool("read", "completed", new JsonObject { ["path"] = "/tmp/a.cs" }),
                Tool("edit", "completed", new JsonObject { ["path"] = "/tmp/a.cs" }))
        });
        messages.Add(new JsonObject
        {
            ["type"] = "assistant",
            ["content"] = new JsonArray(
                Tool("shell", "completed", new JsonObject { ["command"] = "bash -n /tmp/a.cs" }),
                Tool("write", "error", new JsonObject { ["path"] = "/tmp/b.cs" }))
        });
        return messages;
    }

    private static JsonObject Tool(string name, string status, JsonObject input) => new()
    {
        ["type"] = "tool",
        ["name"] = name,
        ["state"] = new JsonObject { ["status"] = status, ["input"] = input }
    };
}
