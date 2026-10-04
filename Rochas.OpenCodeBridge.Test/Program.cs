// ===========================================================================
//  Rochas.OpenCodeBridge.Test — suite unit + e2e da bridge (BCL only, sem NuGet)
//  Runner console: exit 0 = tudo PASS, 1 = algum FAIL.
//  Uso: dotnet run -c Release -- [--bridge URL] [--vllm URL] [--skip-e2e]
//  Unit = traduz/converte via HTTP na bridge (sem modelo). E2E = modelo vivo.
// ===========================================================================

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Orquestração da suite (1 classe: Program; cenários vivem nas *Tests).</summary>
internal static class Program
{
    static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bridge" && i + 1 < args.Length) TestContext.Bridge = args[++i].TrimEnd('/');
            else if (args[i] == "--vllm" && i + 1 < args.Length) TestContext.Vllm = args[++i].TrimEnd('/');
            else if (args[i] == "--skip-e2e") TestContext.SkipE2E = true;
        }
        Console.WriteLine($"[test] bridge={TestContext.Bridge} vllm={TestContext.Vllm} skipE2E={TestContext.SkipE2E}");

        // Descobre o modelo servido p/ ajustar as expectativas (qwen3|coder).
        try
        {
            var st = TestContext.GetObj(TestContext.Bridge + "/api/status").GetAwaiter().GetResult();
            TestContext.ServedModel = st["model"]?.GetValue<string>() ?? "";
            Console.WriteLine($"[test] served={TestContext.ServedModel} coder={TestContext.ServedIsCoder}");
        }
        catch (Exception ex) { Console.WriteLine($"[test] sem status: {ex.Message}"); }

        // ---- unit (bridge, sem modelo)
        TestContext.Run("U-status", UnitStatusTests.Status);
        TestContext.Run("U-metrics", UnitStatusTests.Metrics);
        TestContext.Run("U-models-passthrough", UnitStatusTests.ModelsPassthrough);
        TestContext.Run("U-translate-tools", UnitTranslateTests.TranslateTools);
        TestContext.Run("U-translate-without-stop", UnitTranslateTests.TranslateWithoutStop);
        TestContext.Run("U-convert-thinking-tools", UnitConvertTests.ConvertThinkingTools);
        TestContext.Run("U-translate-multi-namespace", UnitTranslateTests.TranslateMultiNamespace);
        TestContext.Run("U-translate-profiles", UnitTranslateTests.TranslateProfiles);
        TestContext.Run("U-convert-invalid-arguments", UnitConvertTests.ConvertInvalidArguments);

        // ---- e2e (modelo vivo)
        if (!TestContext.SkipE2E)
        {
            // Sonda direta do parser hermes no vLLM: so faz sentido no Qwen3.
            if (!TestContext.ServedIsCoder) TestContext.Run("E-vllm-tool-hermes", E2EToolTests.VllmToolHermes);
            else Console.WriteLine("[test] skip E-vllm-tool-hermes (perfil coder)");
            TestContext.Run("E-responses-simple", E2EToolTests.SimpleResponses);
            TestContext.Run("E-responses-tools", E2EToolTests.ToolResponses);
            TestContext.Run("E-stream-architect", E2EStreamTests.ArchitectStream);
            TestContext.Run("E-review-architect", E2EStreamTests.ArchitectReview);
            TestContext.Run("E-iot-event-queue", E2EArchitectureTests.IotEventQueue);
            TestContext.Run("E-error-mitigation", E2EMitigationTests.ErrorMitigation);
            TestContext.Run("E-full-cycle-iot-queue-push", E2EArchitectureTests.FullCycleIotQueuePush);
            TestContext.Run("E-healthy-concurrency", E2EMitigationTests.HealthyConcurrency);
        }

        return TestContext.Summary();
    }
}
