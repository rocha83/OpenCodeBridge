// ===========================================================================
//  Rochas.OpenCodeBridge.Test — suite unit + e2e da bridge (BCL only, sem NuGet)
//  Runner console: exit 0 = tudo PASS, 1 = algum FAIL.
//  Uso: dotnet run -c Release -- [--bridge URL] [--llama URL] [--skip-e2e]
//  Unit = traduz/converte via HTTP na bridge (sem modelo). E2E = modelo vivo.
// ===========================================================================

using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Orquestração da suite (1 classe: Program; cenários vivem nas *Tests).</summary>
internal static class Program
{
    static int Main(string[] args)
    {
        // O driver WAR tem CLI propria e nao roda a suite; despacha antes do
        // parse normal para nao misturar --war-* com --bridge/--only.
        if (args.Any(a => a.StartsWith("--war", StringComparison.Ordinal)))
            return WarTemplate.RunMain(args);

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--bridge" && i + 1 < args.Length) TestContext.Bridge = args[++i].TrimEnd('/');
            else if (args[i] is "--vllm" or "--llama" && i + 1 < args.Length) TestContext.Llama = args[++i].TrimEnd('/');
            else if (args[i] == "--skip-e2e") TestContext.SkipE2E = true;
            else if (args[i] == "--no-preflight") TestContext.SkipPreflight = true;
            else if (args[i] == "--only" && i + 1 < args.Length) TestContext.Only = args[++i];
            else if (args[i] == "--belts" && i + 1 < args.Length) TestContext.BeltsFilter = args[++i];

            else if (args[i] == "--api-key" && i + 1 < args.Length) TestContext.ApiKey = args[++i];
            else if (args[i] == "--jwt" && i + 1 < args.Length) TestContext.Jwt = args[++i];
            else if (args[i] == "--role" && i + 1 < args.Length) TestContext.Role = args[++i];
            else if (args[i] == "--model" && i + 1 < args.Length) TestContext.Model = args[++i];
            else if (args[i] == "--standalone") TestContext.Standalone = true;
            else if (args[i] == "--standalone-agent" && i + 1 < args.Length) TestContext.StandaloneAgent = args[++i];
            else if (args[i] == "--standalone-model" && i + 1 < args.Length) TestContext.StandaloneModel = args[++i];
            else if (args[i] == "--standalone-timeout" && i + 1 < args.Length) TestContext.StandaloneTimeoutSec = int.Parse(args[++i]);
            else if (args[i] == "--standalone-provider" && i + 1 < args.Length) TestContext.StandaloneProvider = args[++i];
            else if (args[i] == "--dual") TestContext.Dual = true;
            else if (args[i] == "--mode" && i + 1 < args.Length)
            {
                // Dois caminhos, sem alias: bridge (harness main/tails na bridge) e
                // opencode (o cliente traz o master). Qualquer outro valor e erro.
                string m = args[++i];
                if (m is not ("bridge" or "opencode"))
                {
                    Console.WriteLine($"FAIL --mode invalido: {m} (use bridge|opencode)");
                    return 2;
                }
                TestContext.Mode = m;
            }
            else if (args[i] == "--tool-agent-steps" && i + 1 < args.Length) TestContext.ToolAgentSteps = int.Parse(args[++i]);
            else if (args[i] == "--skip-unit") TestContext.SkipUnit = true;
            else if (args[i] == "--no-stop-on-fail") TestContext.StopOnFail = false;
            else if (args[i] == "--front-webdriver" || args[i] == "--webdriver") TestContext.WebDriver = true;
            else if (args[i] == "--webdriver-url" && i + 1 < args.Length) TestContext.WebDriverUrl = args[++i];
            else if (args[i] == "--skip-cpu") TestContext.RunCpu = false;
            else if (args[i] == "--cpu") TestContext.RunCpu = true;
        }
        Console.WriteLine($"[test] bridge={TestContext.Bridge} llama={TestContext.Llama} skipE2E={TestContext.SkipE2E}");
        Console.WriteLine($"[test] dual={TestContext.Dual} mode={TestContext.Mode} role={TestContext.Role} stop_on_fail={TestContext.StopOnFail}");
        string? cred = TestContext.Jwt ?? TestContext.ApiKey;
        if (cred is not null) TestContext.Http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cred);

        // Preflight do stack ANTES de qualquer teste: porta, modelo, upstream,
        // lock e VRAM. O driver WAR marca os filhos com BRIDGE_UNDER_DRIVER=1
        // (o lock e dele); fora do driver, runner concorrente e NO-GO.
        if (!TestContext.SkipPreflight)
        {
            // Linha GPU2 (diagnostico) e' OPCIONAL: pode ser a bridge GPU2
            // (:4125) ou a CPU-forced (:4126). Nome semantico WAR_GPU2_BRIDGE;
            // WAR_CPU_BRIDGE segue aceito como legado. Sem env = rodada Q4-only.
            string? q3 = Environment.GetEnvironmentVariable("WAR_GPU2_BRIDGE")
                         ?? Environment.GetEnvironmentVariable("WAR_CPU_BRIDGE");
            var preflight = StackPreflight.Run(new StackPreflightOptions
            {
                BridgeUrl = TestContext.Bridge,
                LlamaUrl = TestContext.Llama,
                Q3BridgeUrl = q3,
                Q3LlamaUrl = Environment.GetEnvironmentVariable("WAR_UPSTREAM_Q3")
                             ?? "http://127.0.0.1:4110",
                ApiKey = cred,
                RequireQ3 = false,
                RequireMainHarness = TestContext.Mode == "bridge",
                ProbeModel = true,
                CheckLock = Environment.GetEnvironmentVariable("BRIDGE_UNDER_DRIVER") != "1"
            });
            if (!preflight.Print())
                return 3;
        }

        // Descobre o modelo servido p/ ajustar as expectativas (gemma|coder).
        try
        {
            var st = TestContext.GetObj(TestContext.Bridge + "/api/status").GetAwaiter().GetResult();
            TestContext.ServedModel = st["model"]?.GetValue<string>() ?? "";
            TestContext.ServedThinking = st["thinking"]?.GetValue<string>() ?? "events";
            TestContext.ServedLegacy = st["legacy_harness"]?.GetValue<bool>() ?? false;
            Console.WriteLine($"[test] served={TestContext.ServedModel} coder={TestContext.ServedIsCoder} thinking={TestContext.ServedThinking} legacy={TestContext.ServedLegacy}");
        }
        catch (Exception ex) { Console.WriteLine($"[test] sem status: {ex.Message}"); }

        var runnerBins = BinaryGuard.ForRunner();
        Console.WriteLine($"[test] binaries_sha512={runnerBins.Sha512}");
        var rerun = BinaryGuard.Check(runnerBins, BinaryGuard.DefaultStatePath());
        if (!rerun.Allowed)
        {
            Console.WriteLine($"FAIL rerun-guard: {rerun.Reason}");
            return 4;
        }

        // Run manual (fora do driver) comeca com o PAINEL limpo: enunciado,
        // historico e steps da execucao anterior nao vazam para esta rodada —
        // medido em 2026-10-29: linha `E-mission-tools` de um isolado anterior
        // fez `U-diag-reset-panel` contar 2 steps. Na WAR quem zera e' o runner
        // da esteira, UMA vez por nivel/run (`WAR_NO_RESET=1` no step).
        if (Environment.GetEnvironmentVariable("WAR_STEP") is null
            && Environment.GetEnvironmentVariable("WAR_NO_RESET") is null)
            TestContext.ResetDiagnostics(clearProbe: true, required: false,
                apiKey: cred, clearPanel: true);

        // ---- unit (bridge, sem modelo)
        if (!TestContext.SkipUnit)
        {
        TestContext.Run("U-tool-loop-context", UnitToolLoopContextTests.Run);
        TestContext.Run("U-status", UnitStatusTests.Status);
        TestContext.Run("U-metrics", UnitStatusTests.Metrics);
        TestContext.Run("U-diag-reset", UnitStatusTests.DiagReset);
        TestContext.Run("U-diag-reset-panel", UnitStatusTests.DiagResetPanel);
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
        TestContext.Run("U-convert-coerced-narrated-call", UnitConvertTests.ConvertCoercedNarratedCall);
        TestContext.Run("U-convert-coerced-narrated-find", UnitConvertTests.ConvertCoercedNarratedFindExec);
        TestContext.Run("U-convert-coerced-narrated-loose", UnitConvertTests.ConvertCoercedNarratedLooseQuotes);
        TestContext.Run("U-tools-execute", UnitToolsExecuteTests.ToolsExecute);
        TestContext.Run("U-tools-path-hint", UnitToolsExecuteTests.PathHintApontaCaminhoReal);
        TestContext.Run("U-tools-project-path-hint", UnitToolGuardTests.ProjectPathHint);
        TestContext.Run("U-tools-elision-refused", UnitToolGuardTests.ElisionRefused);
        TestContext.Run("U-tools-whole-file-rewrite", UnitToolGuardTests.WholeFileRewriteRefused);
        TestContext.Run("U-tools-artifact-owner", UnitToolGuardTests.ArtifactOwnerAdopted);
        TestContext.Run("U-tool-help", UnitToolHelpTests.Run);
        TestContext.Run("U-tool-feedback-shell-cwd", UnitToolFeedbackTests.ShellReportsCwd);
        TestContext.Run("U-tool-feedback-git-chain", UnitToolFeedbackTests.GitChainTeaching);
        TestContext.Run("U-git-tool-cycle", UnitToolsExecuteTests.GitToolCycle);
        TestContext.Run("U-toolstrategy-execute-shell", UnitToolStrategyTests.ExecuteToShell);
        TestContext.Run("U-toolstrategy-execute-write", UnitToolStrategyTests.ExecuteToWriteSingleQuotes);
        TestContext.Run("U-toolstrategy-bash-to-shell", UnitToolStrategyTests.ExecuteBashMapsToShell);
        TestContext.Run("U-toolstrategy-fs-guide", UnitToolStrategyTests.ExecuteWithFsGetsGuidance);
        TestContext.Run("U-toolstrategy-multicall", UnitToolStrategyTests.ExecuteMultiCallScript);
        TestContext.Run("U-toolstrategy-multicall-cap", UnitToolStrategyTests.ExecuteMultiCallCapped);
        TestContext.Run("U-toolstrategy-nonexecute-intact", UnitToolStrategyTests.NonExecuteStaysIntact);
        TestContext.Run("U-toolstrategy-foreign-namespace", UnitToolStrategyTests.ForeignNamespaceStaysIntact);
        TestContext.Run("U-harness-strip-execute-chat", UnitToolStrategyTests.StripExecuteFromChatTools);
        TestContext.Run("U-harness-strip-execute-responses", UnitToolStrategyTests.StripExecuteFromResponsesTools);
        TestContext.Run("U-harness-code-mode-note", UnitToolStrategyTests.NeutralizeCodeModeWritesDirectToolsNote);
        TestContext.Run("U-harness-quality-core", UnitToolStrategyTests.HarnessTailCarriesQualityCore);
        TestContext.Run("U-main-harness-no-sandbox", UnitMainHarnessTests.NoSandboxInstructions);
        TestContext.Run("U-main-harness-core-tools", UnitMainHarnessTests.CoreToolsOnly);
        TestContext.Run("U-main-harness-skills-env", UnitMainHarnessTests.KeepsSkillsAndEnv);
        TestContext.Run("U-main-harness-no-roles", UnitMainHarnessTests.BaseHasNoRoleSections);
        TestContext.Run("U-main-harness-compact", UnitMainHarnessTests.IsCompact);
        TestContext.Run("U-main-harness-managed", UnitMainHarnessTests.ReplacesManagedSystem);
        TestContext.Run("U-main-harness-role-suffix", UnitMainHarnessTests.AppliesEvenWithRoleSuffix);
        TestContext.Run("U-main-harness-opencode-master", UnitMainHarnessTests.KeepsOpencodeMasterWithoutProfile);
        TestContext.Run("U-main-harness-dynamic", UnitMainHarnessTests.PreservesDynamicBlocks);
        TestContext.Run("U-main-harness-master", UnitMainHarnessTests.DropsMasterButKeepsEnv);
        TestContext.Run("U-main-harness-tools-tail", UnitMainHarnessTests.TailsOnlyWithTools);
        TestContext.Run("U-main-harness-workspace", UnitMainHarnessTests.KeepsWorkspaceFromClient);
        TestContext.Run("U-coding-journey", UnitCodingJourneyTests.AnalyzesBothModes);
        TestContext.Run("U-coding-journey-probe", UnitCodingJourneyTests.StepProbeWritesJourney);
        TestContext.Run("U-sampling-passthrough", UnitSamplingTests.CopiesKnownNumericKeys);
        TestContext.Run("U-sampling-ignores-junk", UnitSamplingTests.IgnoresUnknownAndNonNumeric);
        TestContext.Run("U-sampling-whitelist", UnitSamplingTests.WhitelistHasSamplingOnly);
        TestContext.Run("U-toolargs-valid", UnitToolStrategyTests.SanitizeKeepsValidJson);
        TestContext.Run("U-toolargs-tokens", UnitToolStrategyTests.SanitizeStripsLeakedTokens);
        TestContext.Run("U-toolargs-guided", UnitToolStrategyTests.SanitizeGuidesBrokenArgs);
        TestContext.Run("U-toolargs-missing-keys", UnitToolStrategyTests.SanitizeGuidesMissingRequiredKeys);
        TestContext.Run("U-toolargs-write-ok", UnitToolStrategyTests.SanitizeKeepsCompleteWrite);
        TestContext.Run("U-toolargs-write-missing-content", UnitToolStrategyTests.SanitizeGuidesMissingContent);
        TestContext.Run("U-toolargs-shell-leak", UnitToolStrategyTests.SanitizeShellTrimsLeakedTemplate);
        TestContext.Run("U-toolargs-shell-inline", UnitToolStrategyTests.SanitizeShellPromotesInlineCall);
        TestContext.Run("U-toolargs-subagent", UnitToolStrategyTests.SanitizeGuidesSubagentMissingPrompt);
        TestContext.Run("U-toolargs-skill-ok", UnitToolStrategyTests.SanitizeKeepsSkillWithId);
        // Breaker de loop agentico + escape literal + cauda de tools + temperatura por role.
        TestContext.Run("U-breaker-needs-streak", UnitToolBreakerTests.NeedsStreak);
        TestContext.Run("U-breaker-nudges-same-target", UnitToolBreakerTests.NudgesSameTarget);
        TestContext.Run("U-breaker-trips-at-hard", UnitToolBreakerTests.TripsAtHard);
        TestContext.Run("U-breaker-ignores-distinct-targets", UnitToolBreakerTests.IgnoresDistinctTargets);
        TestContext.Run("U-breaker-normalize-collapses", UnitToolBreakerTests.NormalizeCollapsesMutations);
        TestContext.Run("U-breaker-failure-markers", UnitToolBreakerTests.DetectsFailureMarkers);
        TestContext.Run("U-breaker-compiler-loop", UnitToolBreakerTests.BreaksCompilerEditLoop);
        TestContext.Run("U-breaker-same-error", UnitToolBreakerTests.TripsRepeatedSameError);
        TestContext.Run("U-breaker-interleaved-loop", UnitToolBreakerTests.CountsInterleavedLoop);
        TestContext.Run("U-content-antireply-scopes", UnitToolBreakerTests.ContentScopeUsesHelperSubclasses);
        TestContext.Run("U-flow-gate-no-request", UnitToolBreakerTests.FlowGateNoRequest);
        TestContext.Run("U-flow-gate-applies", UnitToolBreakerTests.FlowGateApplies);
        TestContext.Run("U-tools-antireply-helpers", UnitToolBreakerTests.ToolScopeUsesHelperSubclasses);
        TestContext.Run("U-toolargs-literal-newline", UnitToolBreakerTests.FixesLiteralNewlineEscape);
        TestContext.Run("U-tails-path-simulation-rules", UnitToolBreakerTests.ToolsTailCarriesPathAndSimulationRules);
        TestContext.Run("U-tails-cs-error-table", UnitToolBreakerTests.ToolsTailCarriesCsErrorTable);
        TestContext.Run("U-cs-repair-missing-brace", UnitToolBreakerTests.CsRepairMissingBrace);
        TestContext.Run("U-cs-repair-applies-brace", UnitToolBreakerTests.CsRepairAppliesMissingBrace);
        TestContext.Run("U-cs-repair-unexpected-token", UnitToolBreakerTests.CsRepairUnexpectedToken);
        TestContext.Run("U-cs-hint-counts-call-args", UnitToolBreakerTests.CsHintCountsCallArgs);
        TestContext.Run("U-cs-repair-applies-extra-close", UnitToolBreakerTests.CsRepairAppliesExtraClose);
        TestContext.Run("U-cs-hint-for-in", UnitToolBreakerTests.CsHintDetectsForIn);
        TestContext.Run("U-cs-repair-applies-for-in", UnitToolBreakerTests.CsRepairForInOnlyAfterTwoRepeats);
        TestContext.Run("U-context-compact", UnitToolBreakerTests.ContextCompact);
        TestContext.Run("U-context-compact-recursive", UnitToolBreakerTests.ContextCompactRecursive);
        TestContext.Run("U-context-overflow-detect", UnitToolBreakerTests.ContextOverflowDetect);
        TestContext.Run("U-tails-present-tools", UnitRetryTests.TailsOnlyPresentTools);
        TestContext.Run("U-retry-temp-detour", UnitRetryTests.RetryTemperatureUsesDetourRole);
        TestContext.Run("U-upstream-abort-nudge", UnitRetryTests.AbortNudgeRewritesTurn);
        TestContext.Run("U-upstream-abort-detect", UnitRetryTests.DetectsAbortAndRetryHint);
        TestContext.Run("U-role-permissions", UnitRetryTests.RolePermissionsInSystem);
        TestContext.Run("U-tools-tail-marker", UnitRetryTests.ToolsTailMarker);
        TestContext.Run("U-prompt-dedup", UnitRetryTests.PromptDedupInAgenticLoop);
        // Trilha F# ARQUIVADA (2026-10-30): os pins U-fs-repair-* saíram do placar e
        // vivem em Archived/FsRepairPins.cs; a trilha ativa é C# (CsSyntaxRepair).
        // U-fsmini-covers-repair-codes ARQUIVADO em 2026-10-30 junto da trilha F#:
        // o pin segue em UnitToolBreakerTests (compila), mas nao roda no placar ativo.
        TestContext.Run("U-flow-gate", UnitToolBreakerTests.BridgeFlowGateCases);
        TestContext.Run("U-antirepeat-args-window", UnitToolBreakerTests.AntiRepeatArgsWindowIsIndependent);
        TestContext.Run("U-toolcall-salvage-notice", UnitToolBreakerTests.SalvageNoticeIsRecognizable);
        TestContext.Run("U-belt-scripter-prompt", UnitBeltScenarioTests.ScripterPromptSeparatesDeliverableFromTask);
        TestContext.Run("U-bridge-notice", UnitToolBreakerTests.BridgeNoticeFormatIsSingleSource);
        TestContext.Run("U-tails-tool-first", UnitToolBreakerTests.BehaviorTailForcesToolFirst);
        TestContext.Run("U-translate-temp-respeita-tunel", UnitToolBreakerTests.TranslateTempRespeitaTunel);
        TestContext.Run("U-translate-breaker-verdicts", UnitToolBreakerTests.TranslateBreakerVerdicts);
        TestContext.Run("U-shell-sed-inplace", UnitShellInspectorTests.SedInPlaceDenied);
        TestContext.Run("U-shell-sed-readonly", UnitShellInspectorTests.SedReadOnlyAllowed);
        TestContext.Run("U-shell-bash-payload", UnitShellInspectorTests.BashPayloadDenied);
        TestContext.Run("U-shell-bash-payload-chain", UnitShellInspectorTests.BashPayloadChainAllowed);
        TestContext.Run("U-shell-python-node-payload", UnitShellInspectorTests.PythonAndNodePayloadDenied);
        TestContext.Run("U-shell-pipe-to-shell", UnitShellInspectorTests.PipeToShellDenied);
        TestContext.Run("U-shell-mv-rmdir", UnitShellInspectorTests.MoveAndRmdirDenied);
        // Circuito generico: steps, gate, pausa, retry/restart, build e lock.
        TestContext.Run("U-circuit-step-gate", UnitCircuitTests.StepAndGate);
        TestContext.Run("U-circuit-fail-gate", UnitCircuitTests.FailGate);
        TestContext.Run("U-circuit-resume-done", UnitCircuitTests.ResumeSkipsDone);
        TestContext.Run("U-circuit-battery-stop", UnitCircuitTests.BatteryStopsOnFail);
        TestContext.Run("U-circuit-battery-resume", UnitCircuitTests.BatteryResumesAfterFail);
        TestContext.Run("U-circuit-publish-run", UnitCircuitTests.PublishesRunBeforeVerdict);
        TestContext.Run("U-circuit-restart", UnitCircuitTests.RestartRetries);
        TestContext.Run("U-circuit-pause", UnitCircuitTests.PauseResumes);
        TestContext.Run("U-circuit-lock", UnitCircuitTests.LockExclusive);
        TestContext.Run("U-circuit-build", UnitCircuitTests.BuildIncremental);
        TestContext.Run("U-circuit-deploy", UnitCircuitTests.DeployNotConfigured);
        TestContext.Run("U-circuit-deploy-health", UnitCircuitTests.DeployHealth);
        TestContext.Run("U-circuit-template", UnitCircuitTests.TemplateRunsSteps);
        TestContext.Run("U-circuit-conditional", UnitCircuitTests.ConditionalSteps);
        TestContext.Run("U-circuit-dag", UnitCircuitTests.DependencyOrder);
        TestContext.Run("U-circuit-parallel", UnitCircuitTests.ParallelSteps);
        TestContext.Run("U-circuit-next", UnitCircuitTests.DynamicNextSteps);
        TestContext.Run("U-circuit-gate-policy", UnitCircuitTests.CustomGatePolicy);
        TestContext.Run("U-circuit-multi", UnitCircuitTests.MultiBuildAndDeploy);
        TestContext.Run("U-preflight-go", UnitStackPreflightTests.Go);
        TestContext.Run("U-preflight-main-harness", UnitStackPreflightTests.MainHarnessRequired);
        TestContext.Run("U-preflight-bridge-down", UnitStackPreflightTests.BridgeDown);
        TestContext.Run("U-preflight-model-fail", UnitStackPreflightTests.ModelFails);
        TestContext.Run("U-preflight-q3-upstream", UnitStackPreflightTests.Q3UpstreamMismatch);
        TestContext.Run("U-preflight-lock", UnitStackPreflightTests.LockHeld);
        TestContext.Run("U-preflight-web-login", UnitStackPreflightTests.WebLogin);
        TestContext.Run("U-metrics-deny-counters", UnitMetricsTests.CountersIncrement);
        TestContext.Run("U-metrics-ring3-shell", UnitMetricsTests.ShellDenyIsRing3);
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

        }

        // ---- e2e standalone (opencode real, mesmos enunciados)
        if (TestContext.Standalone)
        {
            TestContext.Run("E-standalone-oi", E2EStandaloneTests.Oi);
            TestContext.Run("E-standalone-tools", E2EStandaloneTests.Tools);
            TestContext.Run("E-standalone-write", E2EStandaloneTests.Write);
            TestContext.Run("E-standalone-devpipe", E2EStandaloneTests.DevPipe);
            TestContext.Run("E-standalone-science-tools", E2EStandaloneTests.ScienceTools);
            TestContext.Run("E-standalone-mission-tools", E2EStandaloneTests.MissionTools);
        }

        // ---- e2e (modelo vivo)
        if (!TestContext.SkipE2E)
        {
        // Provas A/B das flags do harness (stub upstream, sem GPU): falha sem o arg,
        // funciona com o arg ligado. Rodam primeiro e em segundos.
        TestContext.Run("E-flag-context-guard", E2EFlagProofTests.ContextGuard);
        TestContext.Run("E-flag-anti-repeat", E2EFlagProofTests.AntiRepeatRetry);
        TestContext.Run("E-flag-coerce-narrated", E2EFlagProofTests.CoerceNarrated);
        TestContext.Run("E-flag-tail-scope", E2EFlagProofTests.TailScope);
        TestContext.Run("E-science-gpu", E2EScienceTests.ScienceGpu);
        if (TestContext.RunCpu) TestContext.Run("E-science-cpu", E2EScienceTests.ScienceCpu);
        else Console.WriteLine("[test] skip E-science-cpu (--skip-cpu)");
        TestContext.Run("E-mission-tools", E2EMissionTests.MissionTools);
        // Smoke de ctx ANTES das missoes (L4): força o 400 exceed_context_size
        // contra o llama real e prova a escalada da bridge (nudge -> compactacao
        // -> 413). O mecanismo que segura o hanoi, medido em 2s e sem 30 turnos.
        TestContext.Run("E-ctx-overflow", E2EContextOverflowTests.OverflowRecovers);
        TestContext.Run("E-ctx-overflow-exhaust", E2EContextOverflowTests.OverflowExhausted);
        TestContext.Run("E-mission-algo-cs-hanoi", E2EMissionTests.MissionAlgoCsHanoi);
        TestContext.Run("E-mission-algo-cs-bin", E2EMissionTests.MissionAlgoCsBin);
        TestContext.Run("E-mission-algo-cs-btree", E2EMissionTests.MissionAlgoCsBTree);
        TestContext.Run("E-mission-algo-cs-kmeans", E2EMissionTests.MissionAlgoCsKMeans);
        TestContext.Run("E-mission-algo-cs-sort", E2EMissionTests.MissionAlgoCsSort);
        // Trilha F# ARQUIVADA em 2026-10-30: ver Archived/FsAlgoMissions.cs.
        // Nao registrar aqui: teste arquivado nao consome GPU nem entra no placar.
            // Sonda direta do parser hermes no vLLM: so faz sentido no parser nativo.
            if (!TestContext.ServedIsCoder) TestContext.Run("E-vllm-tool-hermes", E2EToolTests.VllmToolHermes);
            else Console.WriteLine("[test] skip E-vllm-tool-hermes (perfil coder)");
            TestContext.Run("E-responses-simple", E2EToolTests.SimpleResponses);
            TestContext.Run("E-responses-tools", E2EToolTests.ToolResponses);
            TestContext.Run("E-devpipe-tools", E2EDevPipeTests.DevPipeTools);
            // Teste de tools completo nos 2 modelos (GPU1 E4B-Q4 + GPU2 E4B-Q3) em paralelo.
            TestContext.Run("E-tools-full", E2EToolsFullTests.FullTools);
            TestContext.Run("E-devpipe-infra", E2EDevPipeTests.DevPipeInfra);
            if (TestContext.NoVision)
                Console.WriteLine("SKIP E-devpipe-vision (upstream sem mmproj)");
            else
            {
                TestContext.Run("E-devpipe-vision", E2EDevPipeTests.DevPipeVision);
                TestContext.Run("E-devpipe-doc", E2EDevPipeTests.DevPipeDoc);
                TestContext.Run("E-devpipe-vision-tool", E2EDevPipeTests.DevPipeVisionTool);
            }
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
            TestContext.Run("E-ent-redis", E2EEnterpriseTests.RedisCircuit);
            TestContext.Run("E-ent-dashboard", E2EEnterpriseTests.ReactDashboard);
            // frontpipe: o 12B gera o front (MVC+React+Bootstrap+Tailwind), o E2B ajusta
            // pelos enunciados e a suite confere os estilos medidos no navegador.
            TestContext.Run("E-front-gen", E2EFrontPipeTests.Gen);
            TestContext.Run("E-front-edit", E2EFrontPipeTests.Edit);
            TestContext.Run("E-front-edit-razor", E2EFrontPipeTests.EditRazor);
            TestContext.Run("E-front-edit-react", E2EFrontPipeTests.EditReact);
            TestContext.Run("E-front-verify", E2EFrontPipeTests.Verify);
            // Scripter 12B compoe script bash (+ valida sintaxe); Scripter E2B gera companion .md; Exec (backlog) le companion e executa.
            TestContext.Run("E-scripter-gen", E2EScripterTests.ScripterGen);
            TestContext.Run("E-companion-gen", E2EScripterTests.CompanionGen);
            TestContext.Run("E-exec-read", E2EScripterTests.ExecValidate);
            // Orch 12B segmenta enunciado em tarefas atomicas (para Scripters 12B + Exec E2B).
            TestContext.Run("E-orch-segment", E2EOrchTests.Segment);
            // Belts (azul→preta) sem navegador: envio direto via bridge pura.
            TestContext.Run("E-belts-direct", E2EBeltsTests.Direct);
            // Belts (azul→preta) Trilha BUILD: 12B build implementa diretamente.
            TestContext.Run("E-belts-build-direct", E2EBeltsBuildTests.Direct);
            TestContext.Run("E-belts-webui", E2EBeltsTests.WebUi);
            // Tools nos DOIS transportes: bridge pura e opencode (--dual roda 2x cada).
            TestContext.RunDual("E-dual-tools", E2EDualToolTests.All);
        }

        int rc = TestContext.Summary();
        if (rc != 0) BinaryGuard.RecordFailure(runnerBins, $"suite FAIL={rc}", BinaryGuard.DefaultStatePath());
        else BinaryGuard.Clear(BinaryGuard.DefaultStatePath());
        return rc;
    }
}
