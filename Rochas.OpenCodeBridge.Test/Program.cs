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
            else if (args[i] == "--only" && i + 1 < args.Length) TestContext.Only = args[++i];
            else if (args[i] == "--api-key" && i + 1 < args.Length) TestContext.ApiKey = args[++i];
            else if (args[i] == "--jwt" && i + 1 < args.Length) TestContext.Jwt = args[++i];
        }
        Console.WriteLine($"[test] bridge={TestContext.Bridge} vllm={TestContext.Vllm} skipE2E={TestContext.SkipE2E}");

        // Descobre o modelo servido p/ ajustar as expectativas (qwen3|coder).
        try
        {
            var st = TestContext.GetObj(TestContext.Bridge + "/api/status").GetAwaiter().GetResult();
            TestContext.ServedModel = st["model"]?.GetValue<string>() ?? "";
            TestContext.ServedThinking = st["thinking"]?.GetValue<string>() ?? "events";
            Console.WriteLine($"[test] served={TestContext.ServedModel} coder={TestContext.ServedIsCoder} thinking={TestContext.ServedThinking}");
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
        TestContext.Run("U-cpu-max-tokens-clamp", UnitTranslateTests.TranslateCpuMaxTokensClamp);
        TestContext.Run("U-convert-invalid-arguments", UnitConvertTests.ConvertInvalidArguments);
        TestContext.Run("U-convert-coerced-text-call", UnitConvertTests.ConvertCoercedTextCall);
        TestContext.Run("U-convert-coerced-angle-call", UnitConvertTests.ConvertCoercedAngleCall);
        TestContext.Run("U-tools-execute", UnitToolsExecuteTests.ToolsExecute);
        TestContext.Run("U-sanitize-drops-reasoning", UnitBypassTests.SanitizeDropsReasoning);        TestContext.Run("U-translate-drops-reasoning", UnitBypassTests.TranslateDropsReasoning);
        TestContext.Run("U-sanitize-system-first", UnitBypassTests.SanitizeSystemFirst);
        TestContext.Run("U-translate-system-first", UnitBypassTests.TranslateSystemFirst);
        TestContext.Run("U-auth-jwt-roundtrip", UnitAuthTests.JwtRoundtrip);
        TestContext.Run("U-auth-jwt-tamper", UnitAuthTests.JwtTamper);
        TestContext.Run("U-auth-jwt-expiry", UnitAuthTests.JwtExpiry);
        TestContext.Run("U-auth-jwt-short-secret", UnitAuthTests.JwtShortSecret);
        TestContext.Run("U-auth-pbkdf2", UnitAuthTests.Pbkdf2);
        TestContext.Run("U-auth-guard-trips", UnitAuthTests.GuardTrips);
        TestContext.Run("U-auth-login-denied", UnitAuthTests.LoginDenied);
        TestContext.Run("U-auth-swagger-jwt-doc", UnitAuthTests.SwaggerJwtDoc);
        TestContext.Run("U-auth-status-guard", UnitAuthTests.StatusGuard);

        // ---- e2e (modelo vivo)
        if (!TestContext.SkipE2E)
        {
            // Sonda direta do parser hermes no vLLM: so faz sentido no Qwen3.
            if (!TestContext.ServedIsCoder) TestContext.Run("E-vllm-tool-hermes", E2EToolTests.VllmToolHermes);
            else Console.WriteLine("[test] skip E-vllm-tool-hermes (perfil coder)");
            TestContext.Run("E-responses-simple", E2EToolTests.SimpleResponses);
            TestContext.Run("E-responses-tools", E2EToolTests.ToolResponses);
            TestContext.Run("E-devpipe-tools", E2EDevPipeTests.DevPipeTools);
            TestContext.Run("E-devpipe-infra", E2EDevPipeTests.DevPipeInfra);
            TestContext.Run("E-devpipe-vision", E2EDevPipeTests.DevPipeVision);
            TestContext.Run("E-devpipe-doc", E2EDevPipeTests.DevPipeDoc);
            TestContext.Run("E-devpipe-vision-tool", E2EDevPipeTests.DevPipeVisionTool);
            TestContext.Run("E-stream-architect", E2EStreamTests.ArchitectStream);
            TestContext.Run("E-review-architect", E2EStreamTests.ArchitectReview);
            TestContext.Run("E-iot-event-queue", E2EArchitectureTests.IotEventQueue);
            TestContext.Run("E-error-mitigation", E2EMitigationTests.ErrorMitigation);
            TestContext.Run("E-full-cycle-iot-queue-push", E2EArchitectureTests.FullCycleIotQueuePush);
            TestContext.Run("E-healthy-concurrency", E2EMitigationTests.HealthyConcurrency);
            TestContext.Run("E-senior-react", E2ESeniorTests.ReactEnterprise);
            TestContext.Run("E-senior-bus", E2ESeniorTests.EventBusBackend);
            TestContext.Run("E-senior-erp", E2ESeniorTests.ErpRules);
            TestContext.Run("E-senior-arm", E2ESeniorTests.ArmIot);
            TestContext.Run("E-ent-crud-product", E2EEnterpriseTests.CrudProduct);
            TestContext.Run("E-ent-crud-customer", E2EEnterpriseTests.CrudCustomer);
            TestContext.Run("E-ent-crud-saleinvoice", E2EEnterpriseTests.CrudSaleInvoice);
            TestContext.Run("E-ent-worker", E2EEnterpriseTests.BackgroundWorker);
            TestContext.Run("E-ent-garnet", E2EEnterpriseTests.GarnetCircuit);
            TestContext.Run("E-ent-dashboard", E2EEnterpriseTests.ReactDashboard);
        }

        return TestContext.Summary();
    }
}
