using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Unit de saúde da bridge (status, métricas, passthrough de modelos).</summary>
internal static class UnitStatusTests
{
    public static async Task Status()
    {
        var s = await TestContext.GetObj(TestContext.Bridge + "/api/status");
        TestContext.Check(s["status"]?.GetValue<string>() == "ok", "status ok");
        TestContext.Check(!string.IsNullOrEmpty(s["model"]?.GetValue<string>()), "model presente");
        TestContext.Check(s["upstream"]?.GetValue<string>() == TestContext.Vllm, "upstream == vllm");
    }

    public static async Task Metrics()
    {
        var m = await TestContext.GetObj(TestContext.Bridge + "/api/metrics");
        TestContext.Check(m["requests"] is not null, "requests presente");
        TestContext.Check(m["tps_output"] is not null && m["tps_total"] is not null, "tps presentes");
    }

    public static async Task ModelsPassthrough()
    {
        var models = await TestContext.GetObj(TestContext.Bridge + "/v1/models");
        var first = models["data"]?[0]?["id"]?.GetValue<string>();
        TestContext.Check(first == TestContext.ServedModel, "passthrough lista servido, veio: " + first);
    }
}
