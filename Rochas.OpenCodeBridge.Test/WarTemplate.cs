// ===========================================================================
//  WarTemplate — a WAR como TEMPLATE do TestCircuitDriver generico.
//
//  Este arquivo nao executa processo, nao controla pausa, nao faz lock e nao
//  grava summary/events: tudo isso e o TestCircuitDriver (TestCircuitDriver.cs).
//  Aqui mora somente a receita WAR:
//    - levels 1..4, steps bridge/opencode/webui;
//    - preflight de llama, bridge, binarios, opencode, disco e GPU;
//    - parser PASS/FAIL dos testes, causa e hash STALE;
//    - build da .slnx e redeploy opt-in;
//    - gate WAR (PASS>0, FAIL=0, STALE=0).
//
//  CLI:
//    dotnet run -c Release -- --war-level 4
//    dotnet run -c Release -- --war-all
//    dotnet run -c Release -- --war-levels 1,2
//    dotnet run -c Release -- --war-level 2 --war-gate-only
//    dotnet run -c Release -- --war-level 4 --war-list
//    dotnet run -c Release -- --war-level 3 --war-only mission-tools
//    dotnet run -c Release -- --war-pause | --war-resume | --war-status
//
//  Portavel: sem bash, flock(2), /proc, nvidia-smi obrigatorio ou paths
//  Linux. Testes adaptam-se a bridge; a bridge nao muda para o teste.
// ===========================================================================

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Test;

internal sealed class WarTemplate : CircuitTemplate
{
    private sealed class WarOptions
    {
        public bool All;
        public bool GateOnly;
        public bool List;
        public bool? RequireGreen;
        public bool? NoVision;
        public bool? Cpu;
        public int Level = 1;
        public int From = 1;
        public int To = 4;
        public string? Levels;
        public string? OnlySteps;
        public string? Stamp;
        public string? ResumeStamp;
        public int ResumeLevel = 1;
        public string? Out;
        public string? Bridge;
        public string? Upstream;
        public string? ApiKey;
        public string? BridgeQ3;
        public string? Blog;
        public string? Modes;
        public string? Lock;
        public string? PauseFile;
        public string? Root;
        public int? StepTimeout;
        public int? Retries;
        public int? RetryDelay;
        public bool RetryAll;
        public bool Rebuild;
        public bool NoBuild;
        public bool StopOnFirstFailure = true;
        // Web UI pelo navegador: front mede com Playwright (--webdriver) e o step
        // webui roda a UI (--web URL --ui). Headed abre a janela (HEADED=1).
        public bool WebDriver;
        public string? WebDriverUrl;
        public bool WebHeaded;
        // Faixas exercitadas pela Web UI (Playwright) no step webui-integracao-topicos.
        public string? WebUiBelts;
        public bool Pause;
        public bool Resume;
        public bool Status;
        public bool Redeploy;
        public string? RedeployStop;
        public string? RedeployStart;
        public List<string> RedeployStopArgs { get; } = new();
        public List<string> RedeployStartArgs { get; } = new();
        public int? RedeployTimeout;
        public string? HealthUrl;
        public bool Help;
    }

    private sealed record WarPaths(
        string Root,
        string OutBase,
        string TestProj,
        string Solution,
        string BridgeDll,
        string TestDll,
        string WebTestDll,
        string Api,
        string Bridge,
        string Upstream,
        string Blog,
        string LockPath,
        string PausePath,
        string? BridgeQ3,
        int StepTimeout,
        int Retries,
        int RetryDelay,
        bool RequireGreen,
        bool NoVision,
        bool Cpu,
        bool NoBuild,
        bool ForceBuild,
        bool RetryAll,
        string[] Modes)
    {
        public static WarPaths From(WarOptions o)
        {
            string root = o.Root ?? Env("LEVEL_ROOT") ?? Env("REPO") ?? FindRoot();
            string outBase = o.Out ?? Env("LEVEL_OUT") ?? Path.Combine(root, "logs", "levels");
            string upstream = (o.Upstream ?? Env("LEVEL_UPSTREAM")
                ?? "http://127.0.0.1:4100").TrimEnd('/');
            bool noVisionDefault = upstream.EndsWith(":4110", StringComparison.Ordinal);
            bool noVision = o.NoVision
                ?? (EnvBool("LEVEL_NO_VISION") ?? noVisionDefault);
            bool requireGreen = o.RequireGreen ?? Env("LEVEL_REQUIRE_GREEN") != "0";
            bool cpu = o.Cpu ?? (Env("LEVEL_CPU") != "0");
            bool noBuild = o.NoBuild || EnvBool("LEVEL_NO_BUILD") == true;
            bool forceBuild = o.Rebuild || EnvBool("LEVEL_REBUILD") == true;
            bool retryAll = o.RetryAll || EnvBool("LEVEL_RETRY_ALL") == true;
            int retries = Math.Clamp(o.Retries ?? ParseInt(Env("LEVEL_RETRIES"), 1), 1, 10);
            int retryDelay = Math.Max(0, o.RetryDelay ?? ParseInt(Env("LEVEL_RETRY_DELAY"), 5));
            int stepTimeout = o.StepTimeout ?? ParseInt(Env("LEVEL_STEP_TIMEOUT"), 7200);

            string modesRaw = (o.Modes ?? Env("LEVEL_MODES") ?? "bridge").Trim();
            string[] modes = modesRaw.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim().ToLowerInvariant())
                .Where(m => m is "opencode" or "bridge")
                .Distinct()
                .ToArray();
            if (modes.Length == 0) modes = new[] { "opencode", "bridge" };

            string testProj = Path.Combine(root, "Rochas.OpenCodeBridge.Test",
                "Rochas.OpenCodeBridge.Test.csproj");
            return new WarPaths(
                Root: root,
                OutBase: outBase,
                TestProj: testProj,
                Solution: FindSolution(root, testProj),
                BridgeDll: Path.Combine(root, "Rochas.OpenCodeBridge", "bin", "Release",
                    "net10.0", "Rochas.OpenCodeBridge.dll"),
                TestDll: Path.Combine(root, "Rochas.OpenCodeBridge.Test", "bin", "Release",
                    "net10.0", "Rochas.OpenCodeBridge.Test.dll"),
                WebTestDll: Path.Combine(root, "Rochas.OpenCodeBridge.Web.Test", "bin",
                    "Release", "net10.0", "Rochas.OpenCodeBridge.Web.Test.dll"),
                Api: o.ApiKey ?? Env("LEVEL_API_KEY") ?? "guest@123",
                Bridge: (o.Bridge ?? Env("LEVEL_BRIDGE") ?? "http://127.0.0.1:4124").TrimEnd('/'),
                Upstream: upstream,
                Blog: o.Blog ?? Env("LEVEL_BLOG")
                    ?? Path.Combine(Path.GetTempPath(), "bridge-gpu.log"),
                LockPath: o.Lock ?? Env("WAR_LOCK")
                    ?? Path.Combine(Path.GetTempPath(), "opencode", "war.lock"),
                PausePath: o.PauseFile ?? Env("WAR_PAUSE")
                    ?? Path.Combine(outBase, "pause"),
                BridgeQ3: o.BridgeQ3 ?? Env("LEVEL_BRIDGE_Q3"),
                StepTimeout: stepTimeout,
                Retries: retries,
                RetryDelay: retryDelay,
                RequireGreen: requireGreen,
                NoVision: noVision,
                Cpu: cpu,
                NoBuild: noBuild,
                ForceBuild: forceBuild,
                RetryAll: retryAll,
                Modes: modes);
        }
    }

    private readonly WarOptions _o;
    private readonly WarPaths _p;
    private readonly int _level;
    private readonly string _out;
    private string _hashGit = "sem-git";
    private string _hashBridge = "";
    private string _hashWeb = "";
    private string _initHashTest = "";
    private string _sha512 = "";
    private readonly List<string> _pfFail = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Regex RxInfra = new(
        "Connection refused|No route to host|Name or service not known|corpo vazio apos retry",
        RegexOptions.CultureInvariant);
    private static readonly Regex RxBridge = new(
        "ERRO: .*|ok=false|permission|negad", RegexOptions.CultureInvariant);
    private static readonly Regex RxFixture = new(
        "artefato .*conteudo=''", RegexOptions.CultureInvariant);
    private static readonly Regex RxLevelDir = new(
        @"^L(\d+)-(.+)$", RegexOptions.CultureInvariant);

    private WarTemplate(WarOptions o, WarPaths p, int level, string stamp,
        string? outDir = null)
    {
        _o = o;
        _p = p;
        _level = level;
        _out = outDir ?? Path.Combine(p.OutBase, $"L{level}-{stamp}");
    }

    public override string Name => $"war-L{_level}";
    public override string Label => $"LEVEL {_level}";
    public string OutDir => _out;
    public string Root => _p.Root;
    public string LockPath => _p.LockPath;
    public string PausePath => _p.PausePath;
    public int StepTimeout => _p.StepTimeout;
    public bool RequireGreen => _p.RequireGreen;

    // ---------------------------------------------------------------------
    // CLI
    // ---------------------------------------------------------------------
    public static int RunMain(string[] args)
    {
        WarOptions o;
        try { o = Parse(args); }
        catch (ArgumentException ex)
        {
            Console.WriteLine($"FAIL {ex.Message}");
            PrintUsage();
            return 2;
        }

        if (o.Help) { PrintUsage(); return 0; }

        WarPaths p = WarPaths.From(o);
        try { return Run(o, p); }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL war-template: {ex.Message}");
            return 1;
        }
    }

    private static int Run(WarOptions o, WarPaths p)
    {
        if (o.Pause) return ControlPause(p);
        if (o.Resume) return ControlResume(p);
        if (o.Status) return PrintStatus(p);
        if (o.All || !string.IsNullOrWhiteSpace(o.Levels) || o.From != 1)
            return RunAll(o, p);
        TestContext.ResetDiagnostics(clearProbe: true, bridge: o.Bridge, required: false, apiKey: o.ApiKey,
            clearPanel: true);
        return RunLevel(o, p, o.Level,
            o.Stamp ?? Env("LEVEL_STAMP") ?? NowStamp(),
            o.GateOnly, lockHeld: false, globalLog: null);
    }

    private static int RunLevel(
        WarOptions o, WarPaths p, int level, string stamp,
        bool gateOnly, bool lockHeld, TextWriter? globalLog)
    {
        var template = new WarTemplate(o, p, level, stamp);
        if (o.List) { template.PrintSteps(); return 0; }

        using var driver = new TestCircuitDriver(new CircuitOptions(
            Root: p.Root,
            RunDirectory: template.OutDir,
            LockPath: p.LockPath,
            PausePath: p.PausePath,
            DefaultTimeoutSeconds: p.StepTimeout,
            RequireGreen: p.RequireGreen,
            AcquireLock: !gateOnly && !lockHeld,
            StopOnFirstFailure: o.StopOnFirstFailure,
            LogSink: globalLog is null ? null : line => globalLog.WriteLine(line)), template);

        if (gateOnly) return GateOnly(template, driver);
        var result = driver.RunAsync().GetAwaiter().GetResult();
        return result.ExitCode;
    }

    private static int GateOnly(WarTemplate template, TestCircuitDriver driver)
    {
        var gate = driver.Gate();
        template.OnRunEnd(driver, gate);
        return template.RequireGreen && !gate.Green ? 1 : 0;
    }

    private static int RunAll(WarOptions o, WarPaths p)
    {
        var levels = LevelsToRun(o);
        if (o.List)
        {
            foreach (int lvl in levels)
                new WarTemplate(o, p, lvl, NowStamp()).PrintSteps();
            return 0;
        }
        if (o.GateOnly) return GateAllDirs(o, p);

        string stamp = o.Stamp ?? Env("LEVEL_STAMP") ?? NowStamp();
        Directory.CreateDirectory(p.OutBase);
        string globalPath = Path.Combine(p.OutBase, $"all-levels-{stamp}.console.log");
        string driverSummary = Path.Combine(p.OutBase, $"driver-summary-{stamp}.tsv");
        using var globalLog = new StreamWriter(globalPath, append: false) { AutoFlush = true };
        File.WriteAllText(driverSummary, "level\trc\tstamp\tout\n");

        using CircuitLock? lockStream = CircuitLock.TryAcquire(p.LockPath);
        if (lockStream is null)
        {
            Console.WriteLine(
                $"BLOCKED: outro runner ativo ({p.LockPath}) — {CircuitLock.DescribeOwner(p.LockPath)}");
            return 3;
        }

        int failed = 0;
        // Sonda zerada UMA vez por bateria (o run isolado ja faz o mesmo por ser a propria rodada).
        TestContext.ResetDiagnostics(clearProbe: true, bridge: o.Bridge, required: false, apiKey: o.ApiKey,
            clearPanel: true);
        LogGlobal(globalLog, $"=== run-all-levels {stamp} (levels: {string.Join(",", levels)}) ===");
        LogGlobal(globalLog, $"repo: {p.Root}");
        LogGlobal(globalLog, $"artefatos: {p.OutBase}");
        LogGlobal(globalLog, o.StopOnFirstFailure
            ? "fail-fast: ON (para no primeiro FAIL/STALE/BLOCKED e nao segue de nivel)"
            : "fail-fast: OFF (--war-no-stop-on-fail: segue mesmo com FAIL)");

        foreach (int lvl in levels)
        {
            string lvlStamp = o.ResumeStamp is not null && lvl == o.ResumeLevel
                ? o.ResumeStamp
                : $"{stamp}-l{lvl}";
            string outDir = Path.Combine(p.OutBase, $"L{lvl}-{lvlStamp}");
            LogGlobal(globalLog, "");
            LogGlobal(globalLog,
                $"############ LEVEL {lvl} ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ############");
            LogGlobal(globalLog, $"OUT: {outDir}");

            int rc = RunLevel(o, p, lvl, lvlStamp, gateOnly: false,
                lockHeld: true, globalLog: globalLog);
            File.AppendAllText(driverSummary, $"{lvl}\t{rc}\t{lvlStamp}\t{outDir}\n");
            LogGlobal(globalLog,
                $"############ LEVEL {lvl} -> rc={rc} ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ############");
            if (rc != 0)
            {
                failed++;
                if (o.StopOnFirstFailure)
                {
                    LogGlobal(globalLog,
                        $"Level {lvl} VERMELHO (rc={rc}) — PARADA fail-fast: nao segue para o proximo nivel.");
                    LogGlobal(globalLog,
                        $"  retomar: dotnet run --project {p.TestProj} -c Release -- --war-level {lvl} --war-stamp {lvlStamp}");
                    break;
                }
                LogGlobal(globalLog,
                    $"Level {lvl} VERMELHO (rc={rc}) — registrado; SEGUINDO para o proximo nivel (--war-no-stop-on-fail).");
                LogGlobal(globalLog,
                    $"  retomar: dotnet run --project {p.TestProj} -c Release -- --war-level {lvl} --war-stamp {lvlStamp}");
            }
            else
            {
                LogGlobal(globalLog, $"Level {lvl} VERDE — seguindo.");
            }
        }

        LogGlobal(globalLog, "");
        if (failed > 0)
        {
            LogGlobal(globalLog,
                $"=== TRILHA CONCLUIDA COM {failed} NIVEL(IS) VERMELHO(S) ===");
            LogGlobal(globalLog, $"resumo: {driverSummary}");
            return 1;
        }
        LogGlobal(globalLog,
            $"=== TODOS OS LEVELS DA TRILHA VERDES ({DateTime.Now:yyyy-MM-dd HH:mm:ss}) ===");
        return 0;
    }

    private static int GateAllDirs(WarOptions o, WarPaths p)
    {
        if (!Directory.Exists(p.OutBase))
        {
            Console.WriteLine($"sem artefatos em {p.OutBase}");
            return 0;
        }
        foreach (string dir in Directory.GetDirectories(p.OutBase, "L*-*")
                     .OrderBy(d => d, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(dir);
            var m = RxLevelDir.Match(name);
            if (!m.Success) continue;
            if (!File.Exists(Path.Combine(dir, "summary.tsv"))) continue;

            Console.WriteLine($"--- gate {dir}");
            int level = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string stamp = m.Groups[2].Value;
            var template = new WarTemplate(o, p, level, stamp, dir);
            using var driver = new TestCircuitDriver(new CircuitOptions(
                Root: p.Root,
                RunDirectory: dir,
                LockPath: p.LockPath,
                PausePath: p.PausePath,
                DefaultTimeoutSeconds: p.StepTimeout,
                RequireGreen: p.RequireGreen,
                AcquireLock: false), template);
            var gate = driver.Gate();
            template.OnRunEnd(driver, gate);
        }
        return 0;
    }

    // ---------------------------------------------------------------------
    // Template: run_start, build, deploy, preflight, steps, run_end
    // ---------------------------------------------------------------------
    public override void OnRunStart(TestCircuitDriver driver)
    {
        _hashGit = GitHash();
        _hashBridge = Hash(_p.BridgeDll);
        _hashWeb = Hash(_p.WebTestDll);
        _initHashTest = Hash(_p.TestDll);
        _sha512 = BinaryGuard.ForWar(_p.BridgeDll, _p.TestDll, _p.WebTestDll).Sha512;
        // step_end -> bridge (painel); o driver nao conhece a bridge.
        Environment.SetEnvironmentVariable("WAR_EVENT_URL", _p.Bridge + "/api/test/step");
        Environment.SetEnvironmentVariable("WAR_EVENT_KEY", _p.Api);

        driver.Publish(new JsonObject
        {
            ["t"] = "run_start",
            ["level"] = _level,
            ["stamp"] = StampOf(_out),
            ["git"] = _hashGit,
            ["sha512"] = _sha512,
            ["dlls"] = new JsonObject
            {
                ["bridge"] = _hashBridge,
                ["test"] = _initHashTest,
                ["web"] = _hashWeb
            },
            ["bridge"] = _p.Bridge,
            ["upstream"] = _p.Upstream,
            ["no_vision"] = _p.NoVision,
            ["q3_bridge"] = _p.BridgeQ3 ?? ""
        });
        driver.Log($"=== LEVEL {_level} ({_out}) ===");
    }

    public override CircuitBuildSpec? BuildSpec(TestCircuitDriver driver)
    {
        if (_p.NoBuild)
        {
            driver.Log("build: desligado (--war-no-build)");
            return null;
        }

        var outputs = new[] { _p.TestDll, _p.BridgeDll, _p.WebTestDll };
        var inputs = new[]
        {
            Path.Combine(_p.Root, "Rochas.OpenCodeBridge"),
            Path.Combine(_p.Root, "Rochas.OpenCodeBridge.Test"),
            Path.Combine(_p.Root, "Rochas.OpenCodeBridge.Web"),
            Path.Combine(_p.Root, "Rochas.OpenCodeBridge.Web.Test"),
            Path.Combine(_p.Root, "Rochas.OpenCodeBridge.Runner")
        };
        return new CircuitBuildSpec(
            Name: "build-slnx",
            Command: new CircuitCommand(
                Execution.Resolve("dotnet"),
                new[] { "build", _p.Solution, "-c", "Release", "--nologo", "-v:minimal" },
                _p.Root, ChildEnv()),
            Outputs: outputs,
            InputRoots: inputs,
            TimeoutSeconds: 1800,
            Force: _p.ForceBuild);
    }

    public override void OnBuildCompleted(TestCircuitDriver driver, CircuitBuildResult result)
        => RefreshHashes(driver);

    public override CircuitDeploymentSpec? DeploymentSpec(TestCircuitDriver driver)
    {
        if (!_o.Redeploy) return null;
        if (string.IsNullOrWhiteSpace(_o.RedeployStart))
            throw new InvalidOperationException("--war-redeploy exige --war-redeploy-start");

        CircuitCommand? stop = string.IsNullOrWhiteSpace(_o.RedeployStop)
            ? null
            : new CircuitCommand(
                Execution.Resolve(_o.RedeployStop), _o.RedeployStopArgs,
                _p.Root, ChildEnv());
        var start = new CircuitCommand(
            Execution.Resolve(_o.RedeployStart), _o.RedeployStartArgs,
            _p.Root, ChildEnv());
        return new CircuitDeploymentSpec(
            Name: "bridge-redeploy",
            Stop: stop,
            Start: start,
            Healthy: _ => Task.FromResult(HealthOk()),
            TimeoutSeconds: _o.RedeployTimeout ?? 120,
            IntervalMs: 1000);
    }

    public override void OnDeployCompleted(
        TestCircuitDriver driver, CircuitDeploymentResult result) => RefreshHashes(driver);

    public override Task<bool> PreflightAsync(TestCircuitDriver driver, CancellationToken ct)
        => Task.FromResult(Preflight(driver));

    public override IEnumerable<CircuitStepSpec> Steps(TestCircuitDriver driver)
    {
        foreach (CircuitStepSpec step in AllSteps(driver))
            if (MatchesStep(step.Name))
                yield return step;
    }

    /// <summary>Filtro --war-only: substring (ou lista com virgula) do nome do step.</summary>
    private bool MatchesStep(string name)
    {
        if (string.IsNullOrWhiteSpace(_o.OnlySteps)) return true;
        foreach (string token in _o.OnlySteps.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private IEnumerable<CircuitStepSpec> AllSteps(TestCircuitDriver driver)
    {
        switch (_level)
        {
            // L1: nucleo + integracao + topicos. Units primeiro (fail-fast
            // barato); depois os cenarios de arquitetura e de dominio.
            case 1:
                // Units sao da bridge (HTTP), nao do opencode: roda uma vez.
                yield return StepBridge(driver, "units", "bridge",
                    new[] { "--only", "U-" });
                foreach (string mode in _p.Modes)
                {
                    yield return StepBridge(driver, $"integration-{mode}", mode,
                        new[] { "--only", "E-stream,E-review,E-iot,E-error,E-full-cycle,E-healthy" });
                    yield return StepBridge(driver, $"topics-{mode}", mode,
                        new[] { "--only", "E-senior,E-ent" });
                }
                break;

            // L2: tools completas + belts azul/verde (build e orch).
            case 2:
                foreach (string mode in _p.Modes)
                {
                    yield return StepBridge(driver, $"responses-tools-{mode}", mode,
                        new[] { "--only", "E-responses-tools" });
                    yield return StepBridge(driver, $"tools-full-{mode}", mode,
                        new[] { "--only", "E-tools-full" });
                    yield return StepBridge(driver, $"belts-build-{mode}", mode,
                        new[] { "--only", "E-belts-build-direct", "--belts", "azul,verde" });
                    yield return StepBridge(driver, $"belts-orch-{mode}", mode,
                        new[] { "--only", "E-belts-direct", "--belts", "azul,verde" });
                }
                break;

            // L3: cientifico GPU + mission tools + belt roxa (build e orch).
            case 3:
                foreach (string mode in _p.Modes)
                {
                    yield return StepBridge(driver, $"science-gpu-{mode}", mode,
                        new[] { "--only", "E-science-gpu" });
                    yield return StepBridge(driver, $"mission-tools-{mode}", mode,
                        new[] { "--only", "E-mission-tools" });
                    yield return StepBridge(driver, $"belts-build-roxa-{mode}", mode,
                        new[] { "--only", "E-belts-build-direct", "--belts", "roxa" });
                    yield return StepBridge(driver, $"belts-orch-roxa-{mode}", mode,
                        new[] { "--only", "E-belts-direct", "--belts", "roxa" });
                }
                break;

            // L4: mission algo em C# (hanoi + bin/btree/kmeans/sort) + belts
            // marrom/preta + frontend e Web UI. A trilha F# (bin/btree/sort) foi
            // ARQUIVADA em 2026-10-30 (ver Archived/FsAlgoMissions.cs): o modelo
            // alternava PASS/FAIL no mesmo enunciado e o FAIL bloqueava o L4.
            case 4:
                foreach (string mode in _p.Modes)
                {
                    yield return StepBridge(driver, $"algo-cs-hanoi-{mode}", mode,
                        new[] { "--only", "=E-mission-algo-cs-hanoi" });
                    yield return StepBridge(driver, $"algo-cs-bin-{mode}", mode,
                        new[] { "--only", "E-mission-algo-cs-bin" });
                    yield return StepBridge(driver, $"algo-cs-btree-{mode}", mode,
                        new[] { "--only", "E-mission-algo-cs-btree" });
                    yield return StepBridge(driver, $"algo-cs-kmeans-{mode}", mode,
                        new[] { "--only", "E-mission-algo-cs-kmeans" });
                    yield return StepBridge(driver, $"algo-cs-sort-{mode}", mode,
                        new[] { "--only", "E-mission-algo-cs-sort" });
                    yield return StepBridge(driver, $"belts-build-final-{mode}", mode,
                        new[] { "--only", "E-belts-build-direct", "--belts", "marrom,preta" });
                    yield return StepBridge(driver, $"belts-orch-final-{mode}", mode,
                        new[] { "--only", "E-belts-direct", "--belts", "marrom,preta" });
                    yield return StepBridge(driver, $"front-{mode}", mode,
                        _o.WebDriver
                            ? new[] { "--only", "E-front", "--webdriver", "--webdriver-url", WebUrl() }
                            : new[] { "--only", "E-front" });
                }
                yield return StepWeb(driver);
                break;

            default:
                throw new ArgumentException($"level invalido: {_level} (use 1..4)");
        }
    }

    public override void OnRunEnd(TestCircuitDriver driver, CircuitGateResult gate)
    {
        if (gate.Green)
            BinaryGuard.Clear(Path.Combine(_p.Root, "logs", ".rerun-guard.json"));
        driver.Publish(new JsonObject
        {
            ["t"] = "run_end",
            ["level"] = _level,
            ["stamp"] = StampOf(_out),
            ["pass"] = gate.Pass,
            ["fail"] = gate.Fail,
            ["skip"] = gate.Skip,
            ["stale"] = gate.Stale,
            ["green"] = gate.Green
        });
        driver.Log($"artefatos: {_out} (events.jsonl + summary.tsv)");
        // Nao deixa a env vazar para os testes unitarios do proximo nivel:
        // sem isso, UnitCircuitTests publica steps fake (bad/once/p1...) no
        // /api/test/step e eles aparecem em "Steps concluidos".
        Environment.SetEnvironmentVariable("WAR_EVENT_URL", null);
        Environment.SetEnvironmentVariable("WAR_EVENT_KEY", null);
    }

    // ---------------------------------------------------------------------
    // Steps WAR
    // ---------------------------------------------------------------------
    private CircuitStepSpec StepBridge(
        TestCircuitDriver driver, string name, string mode, string[] extra)
    {
        var args = new List<string>
        {
            "run", "--project", _p.TestProj, "-c", "Release", "--no-build", "--",
            "--api-key", _p.Api, "--bridge", _p.Bridge
        };
        args.AddRange(extra);
        if (!extra.Contains("--mode", StringComparer.Ordinal))
        {
            args.Add("--mode");
            args.Add(mode);
        }

        var env = ChildEnv();
        env["WAR_STEP"] = name;
        return new CircuitStepSpec(
            Name: name,
            Command: new CircuitCommand(
                Execution.Resolve("dotnet"), args, _p.Root, env),
            TimeoutSeconds: _p.StepTimeout,
            Mode: mode,
            RunTag: _hashGit,
            FollowLogPath: _p.Blog,
            FollowLogOutputPath: Path.Combine(driver.StepsDirectory, name + ".bridge.log"),
            ProbeUrl: _p.Bridge,
            ProbeApiKey: _p.Api,
            ProbeLogs: new[] { _p.Blog },
            Restart: RestartPolicy(),
            Verdict: run => VerdictForTest(driver, run));
    }

    /// <summary>URL da Web UI do step de integracao (<c>--war-webdriver-url</c>).</summary>
    private string WebUrl() => string.IsNullOrWhiteSpace(_o.WebDriverUrl)
        ? "http://127.0.0.1:4130"
        : _o.WebDriverUrl!;

    private CircuitStepSpec StepWeb(TestCircuitDriver driver)
    {
        const string name = "webui-integracao-topicos";
        if (!File.Exists(_p.WebTestDll))
            driver.Log("ERRO: Web.Test nao buildado");
        // --war-webdriver: a Web UI e' exercitada pelo navegador (Playwright), com a
        // janela aberta quando --war-headed. Sem a flag o step roda so' unit+integracao
        // HTTP, como antes.
        string webUiBelts = string.IsNullOrWhiteSpace(_o.WebUiBelts) ? "azul" : _o.WebUiBelts!;
        var webArgs = _o.WebDriver
            ? new[]
            {
                "exec", _p.WebTestDll, "--web", WebUrl(),
                // phase "ui": uma janela por faixa, enviando o enunciado pela propria
                // tela do /Chat (fase "full" nao existe no switch da BeltUiTests).
                "--ui", "--phase", "ui", "--belts", webUiBelts,
                // smoke deterministico do hello (login + /Chat + engine) na mesma janela.
                "--simple"
            }
            : new[] { "exec", _p.WebTestDll };
        driver.Log(_o.WebDriver
            ? $"[webui] Playwright em {WebUrl()} (headed={(_o.WebHeaded ? "1" : "0")}, belts={webUiBelts})"
            : "[webui] sem --war-webdriver: so' unit + integracao HTTP");
        return new CircuitStepSpec(
            Name: name,
            Command: new CircuitCommand(
                Execution.Resolve("dotnet"),
                webArgs, _p.Root, ChildEnv()),
            TimeoutSeconds: _p.StepTimeout,
            Mode: "web",
            RunTag: _hashGit,
            Restart: RestartPolicy(),
            Verdict: run => VerdictForWeb(driver, run));
    }

    private CircuitRestartPolicy RestartPolicy()
    {
        return new CircuitRestartPolicy(
            MaxAttempts: _p.Retries,
            DelaySeconds: _p.RetryDelay,
            ShouldRetry: (run, outcome) =>
                outcome.Verdict == CircuitVerdict.Fail
                && (_p.RetryAll || run.ExitCode is 124 or 137));
    }

    private CircuitStepOutcome VerdictForTest(
        TestCircuitDriver driver, CircuitStepRun run)
    {
        CircuitStepOutcome outcome = ParseLogVerdict(run.LogPath, run.ExitCode);
        if (Hash(_p.TestDll) != _initHashTest)
        {
            string now = Hash(_p.TestDll);
            driver.Publish(new JsonObject
            {
                ["t"] = "step_stale",
                ["step"] = run.Name,
                ["test_sha_inicio"] = _initHashTest,
                ["test_sha_agora"] = now
            });
            _initHashTest = now;
            return CircuitStepOutcome.Stale("binario mudou durante o run");
        }
        if (outcome.Verdict == CircuitVerdict.Fail)
            BinaryGuard.RecordFailure(
                new BinaryGuard.Binaries(_sha512, _p.BridgeDll + "\n" + _p.TestDll + "\n" + _p.WebTestDll),
                $"step {run.Name} FAIL: {outcome.Cause}",
                Path.Combine(_p.Root, "logs", ".rerun-guard.json"));
        return outcome;
    }

    private CircuitStepOutcome VerdictForWeb(
        TestCircuitDriver driver, CircuitStepRun run)
    {
        CircuitStepOutcome outcome = ParseLogVerdict(run.LogPath, run.ExitCode);
        if (Hash(_p.WebTestDll) != _hashWeb)
        {
            string now = Hash(_p.WebTestDll);
            driver.Publish(new JsonObject
            {
                ["t"] = "step_stale",
                ["step"] = run.Name,
                ["web_sha_inicio"] = _hashWeb,
                ["web_sha_agora"] = now
            });
            _hashWeb = now;
            return CircuitStepOutcome.Stale("binario web mudou durante o run");
        }
        if (outcome.Verdict == CircuitVerdict.Fail)
            BinaryGuard.RecordFailure(
                new BinaryGuard.Binaries(_sha512, _p.BridgeDll + "\n" + _p.TestDll + "\n" + _p.WebTestDll),
                $"step {run.Name} FAIL: {outcome.Cause}",
                Path.Combine(_p.Root, "logs", ".rerun-guard.json"));
        return outcome;
    }

    private static CircuitStepOutcome ParseLogVerdict(string logPath, int rc)
    {
        string verdict = "SKIP";
        if (File.Exists(logPath))
        {
            try
            {
                foreach (string line in File.ReadLines(logPath))
                {
                    if (line.StartsWith("FAIL ", StringComparison.Ordinal))
                    {
                        verdict = "FAIL";
                        break;
                    }
                    if (line.StartsWith("PASS ", StringComparison.Ordinal)) verdict = "PASS";
                }
            }
            catch
            {
                verdict = "FAIL";
            }
        }
        if (rc != 0 && verdict == "PASS") verdict = "FAIL";
        return verdict switch
        {
            "PASS" => CircuitStepOutcome.Pass,
            "FAIL" => CircuitStepOutcome.Fail(Classify(logPath, rc)),
            _ => CircuitStepOutcome.Skip()
        };
    }

    private static string Classify(string logPath, int rc)
    {
        if (rc is 124 or 137) return "timeout";
        string text = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
        if (RxInfra.IsMatch(text)) return "infra";
        if (text.Contains("calls=)", StringComparison.Ordinal)) return "executor";
        if (RxBridge.IsMatch(text)) return "bridge";
        if (RxFixture.IsMatch(text)) return "fixture";
        return "undetermined";
    }

    // ---------------------------------------------------------------------
    // Preflight WAR
    // ---------------------------------------------------------------------
    private bool Preflight(TestCircuitDriver driver)
    {
        _pfFail.Clear();
        driver.Log("=== preflight ===");

        int upPort = PortOf(_p.Upstream, 4100);
        var health = HttpGet($"http://127.0.0.1:{upPort}/health", null);
        bool healthOk = health.Code == 200
            && health.Body.Contains("\"ok\"", StringComparison.Ordinal);
        Pf(driver, $"llama_{upPort}", healthOk, new JsonObject
        {
            ["port"] = upPort,
            ["health"] = healthOk ? "ok" : health.Body.Trim()
        });

        string nctx = "", slots = "", modelPath = "";
        var props = HttpGet($"http://127.0.0.1:{upPort}/props", _p.Api);
        if (props.Code == 200)
        {
            var j = TryJson(props.Body);
            nctx = Str(j, "n_ctx");
            slots = Str(j, "total_slots");
            modelPath = Str(j, "model_path");
        }
        Pf(driver, $"llama_{upPort}_props", props.Code == 200, new JsonObject
        {
            ["port"] = upPort,
            ["n_ctx"] = nctx,
            ["slots"] = slots,
            ["model_path"] = FileName(modelPath)
        });

        var status = HttpGet($"{_p.Bridge}/api/status", _p.Api);
        var st = status.Code == 200 ? TryJson(status.Body) : null;
        string model = Str(st, "model");
        string bup = Str(st, "upstream");
        string bmain = Str(st, "main_harness");
        string btails = Str(st, "harness_tails");
        string bctx = Str(st, "context_tokens");
        string btemp = Str(st, "temperature");
        if (string.IsNullOrEmpty(model))
        {
            Pf(driver, "bridge_status", false, new JsonObject
            {
                ["bridge"] = _p.Bridge,
                ["http"] = status.Code
            });
        }
        else
        {
            Pf(driver, "bridge_status", true, new JsonObject
            {
                ["bridge"] = _p.Bridge,
                ["model"] = model,
                ["upstream"] = bup,
                ["temperature"] = btemp,
                ["main_harness"] = bmain,
                ["harness_tails"] = btails,
                ["context_tokens"] = bctx
            });
            // Gate duro: o WAR sempre roda steps de bridge (L1..L4). Bridge
            // gerido sem MainHarness = modelo orfao de base, so tails. Nao e
            // aviso: aborta o preflight com a correcao explicita. O modo
            // opencode puro (fora do WAR) pode viver sem a base, porque usa o
            // harness do proprio CLI.
            Pf(driver, "bridge_main_harness",
                string.Equals(bmain, "true", StringComparison.OrdinalIgnoreCase),
                new JsonObject
                {
                    ["bridge"] = _p.Bridge,
                    ["main_harness"] = bmain,
                    ["fix"] = "ligue --main-harness 1 na unit do bridge (WAR roda steps de bridge)"
                });

            // REGRA PRIMORDIAL: cauda de tools e obrigatoria para todo teste que
            // ENUNCIA uso de tools. Sem ela o modelo perde o contrato operacional
            // (read/write/edit/glob/...), que nao vai duplicado na prosa. O gate
            // e o deployment: --harness-tails 1 e --tail-scope != off.
            string bscope = Str(st, "tail_scope");
            Pf(driver, "bridge_harness_tails",
                string.Equals(btails, "true", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(bscope, "off", StringComparison.OrdinalIgnoreCase),
                new JsonObject
                {
                    ["bridge"] = _p.Bridge,
                    ["harness_tails"] = btails,
                    ["tail_scope"] = bscope,
                    ["fix"] = "ligue --harness-tails 1 --tail-scope all|managed (tool tails e regra para teste que usa tools)"
                });
        }

        Pf(driver, "bridge_upstream",
            string.IsNullOrEmpty(bup) || bup == _p.Upstream,
            new JsonObject
            {
                ["bridge_upstream"] = string.IsNullOrEmpty(bup) ? _p.Upstream : bup,
                ["esperado"] = _p.Upstream
            });
        Pf(driver, "bridge_context_tokens",
            string.IsNullOrEmpty(nctx) || string.IsNullOrEmpty(bctx) || nctx == bctx,
            new JsonObject { ["context_tokens"] = bctx, ["n_ctx_slot"] = nctx });

        // Q3 nao faz parte do gate do Q4: checagem apenas informativa. A belt
        // belts-orch chama :4125 por design, mas Q3 fora nao bloqueia o run.
        string q3Bridge = (_p.BridgeQ3 ?? "http://127.0.0.1:4125").TrimEnd('/');
        if (!string.Equals(q3Bridge, _p.Bridge.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            var q3status = HttpGet($"{q3Bridge}/api/status", _p.Api);
            var q3j = q3status.Code == 200 ? TryJson(q3status.Body) : null;
            string q3model = Str(q3j, "model");
            string q3up = Str(q3j, "upstream");
            driver.Log($"AVISO: Q3 {q3Bridge} http={q3status.Code} model={q3model} upstream={q3up}"
                + (string.IsNullOrEmpty(q3model) ? " (fora: belts-orch pode falhar)" : ""));
        }

        Pf(driver, "test_binario", File.Exists(_p.TestDll),
            new JsonObject { ["sha"] = _initHashTest, ["path"] = _p.TestDll });
        Pf(driver, "webtest_binario", File.Exists(_p.WebTestDll),
            new JsonObject { ["sha"] = _hashWeb, ["path"] = _p.WebTestDll });

        var rerunGuard = BinaryGuard.Check(
            new BinaryGuard.Binaries(_sha512, _p.BridgeDll + "\n" + _p.TestDll + "\n" + _p.WebTestDll),
            Path.Combine(_p.Root, "logs", ".rerun-guard.json"));
        Pf(driver, "rerun_sha512", rerunGuard.Allowed, new JsonObject
        {
            ["sha512"] = _sha512.Length >= 16 ? _sha512[..16] : _sha512,
            ["motivo"] = rerunGuard.Reason
        });

        // REGRA DO CRIADOR: fail-fast e guard de rebuild andam juntos. Com
        // --war-stop-on-fail o guard e obrigatorio (nao existe flag para
        // desligar); sem ele um PASS poderia sair com binario velho. A
        // evidencia carrega o modo do driver que esta sendo verificado.
        Pf(driver, "guard_ativo_fail_fast", !_o.StopOnFirstFailure || rerunGuard.Allowed, new JsonObject
        {
            ["fail_fast"] = _o.StopOnFirstFailure,
            ["guard"] = "regeracao-por-sha512",
            ["modo"] = string.IsNullOrWhiteSpace(_o.Modes) ? "bridge" : _o.Modes,
            ["estado"] = rerunGuard.Allowed ? "ok" : rerunGuard.Reason
        });

        string? opencode = Execution.FindOnPath("opencode");
        string version = "";
        if (opencode is not null)
        {
            var v = Execution.Run(
                new CircuitCommand(opencode, new[] { "--version" }, _p.Root, null), 5);
            version = v.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? "";
        }
        Pf(driver, "opencode_cli", opencode is not null, new JsonObject
        {
            ["path"] = opencode ?? "",
            ["versao"] = version
        });

        var orphans = Execution.FindProcessIds("Rochas.OpenCodeBridge.Test");
        Pf(driver, "sem_run_orfao", orphans.Count == 0, new JsonObject
        {
            ["pids"] = string.Join(" ", orphans)
        });

        long freeMb = 0;
        try
        {
            string rootPath = Path.GetPathRoot(Path.GetFullPath(_out)) ?? "/";
            freeMb = new DriveInfo(rootPath).AvailableFreeSpace / 1024 / 1024;
        }
        catch
        {
            // Sem DriveInfo: evidencia 0.
        }
        Pf(driver, "disco", freeMb >= 2048, new JsonObject { ["livre_mb"] = freeMb });

        string? nvidia = Execution.FindOnPath("nvidia-smi");
        if (nvidia is null)
        {
            Pf(driver, "gpu", true, new JsonObject { ["uso"] = "n/d" });
        }
        else
        {
            var gpu = Execution.Run(new CircuitCommand(nvidia, new[]
            {
                "--query-gpu=memory.used,memory.total", "--format=csv,noheader"
            }, _p.Root, null), 5);
            Pf(driver, "gpu", true, new JsonObject { ["uso"] = gpu.Output.Trim() });
        }

        if (_pfFail.Count > 0)
        {
            driver.Publish(new JsonObject
            {
                ["t"] = "preflight_fail",
                ["checks"] = string.Join(",", _pfFail)
            });
            driver.Log($"preflight FALHOU: {string.Join(",", _pfFail)} (veredito BLOCKED)");
            return false;
        }
        driver.Log("preflight ok");
        return true;
    }

    private void Pf(TestCircuitDriver driver, string check, bool ok, JsonObject evidence)
    {
        driver.Publish(new JsonObject
        {
            ["t"] = "preflight",
            ["check"] = check,
            ["ok"] = ok,
            ["evidence"] = evidence
        });
        if (!ok) _pfFail.Add(check);
    }

    // ---------------------------------------------------------------------
    // Helpers WAR
    // ---------------------------------------------------------------------
    private void RefreshHashes(TestCircuitDriver driver)
    {
        string bridge = Hash(_p.BridgeDll);
        string test = Hash(_p.TestDll);
        string web = Hash(_p.WebTestDll);
        if (bridge != _hashBridge || test != _initHashTest || web != _hashWeb)
        {
            driver.Publish(new JsonObject
            {
                ["t"] = "dlls_ready",
                ["bridge"] = bridge,
                ["test"] = test,
                ["web"] = web
            });
        }
        _hashBridge = bridge;
        _initHashTest = test;
        _hashWeb = web;
    }

    private bool HealthOk()
    {
        string url = _o.HealthUrl ?? (_p.Bridge + "/api/status");
        return HttpGet(url, _p.Api).Code == 200;
    }

    private Dictionary<string, string> ChildEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WAR_UPSTREAM"] = _p.Upstream,
            ["WAR_NO_VISION"] = _p.NoVision ? "1" : "0",
            // Stream history por step: o TestContext exporta a sessao opencode
            // e grava a jornada bridge; a sonda (StepProbe) le e publica histogramas.
            ["WAR_JOURNEY_DIR"] = Path.Combine(_out, "journey"),
            // Filhos do driver rodam sob o lock dele: o preflight da suite nao
            // deve tratar o lock do proprio driver como runner concorrente.
            ["BRIDGE_UNDER_DRIVER"] = "1"
        };
        if (!string.IsNullOrWhiteSpace(_p.BridgeQ3)) env["WAR_CPU_BRIDGE"] = _p.BridgeQ3!;
        // Playwright: HEADED=1 e' lido por PlaywrightSetup.Headless() no Web.Test.
        if (_o.WebHeaded) env["HEADED"] = "1";
        return env;
    }

    private string GitHash()
    {
        var r = Execution.Run(new CircuitCommand(
            Execution.Resolve("git"),
            new[] { "-C", _p.Root, "rev-parse", "--short", "HEAD" },
            _p.Root, null), 5);
        return r.Code == 0 ? r.Output.Trim() : "sem-git";
    }

    private static string FindSolution(string root, string testProj)
    {
        string preferred = Path.Combine(root, "Rochas.OpenCodeBridge.slnx");
        if (File.Exists(preferred)) return preferred;
        try
        {
            string? any = Directory.EnumerateFiles(root, "*.slnx")
                .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (any is not null) return any;
        }
        catch
        {
            // Sem slnx: builda o projeto de teste.
        }
        return testProj;
    }

    private static (int Code, string Body) HttpGet(string url, string? bearer)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(bearer))
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            using var resp = Http.Send(req);
            using var sr = new StreamReader(resp.Content.ReadAsStream());
            return ((int)resp.StatusCode, sr.ReadToEnd());
        }
        catch (Exception ex)
        {
            return (0, ex.Message);
        }
    }

    private static JsonObject? TryJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch { return null; }
    }

    private static string Str(JsonObject? obj, string key)
    {
        var n = obj?[key];
        if (n is null) return "";
        if (n is JsonValue v)
        {
            if (v.TryGetValue<string>(out string? s)) return s;
            return v.ToJsonString().Trim('"');
        }
        return n.ToJsonString();
    }

    private static string FileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return Path.GetFileName(path); }
        catch { return path; }
    }

    private static int PortOf(string url, int fallback)
    {
        try
        {
            var u = new Uri(url);
            return u.Port > 0 ? u.Port : fallback;
        }
        catch
        {
            int idx = url.LastIndexOf(':');
            return idx >= 0 && int.TryParse(url[(idx + 1)..].TrimEnd('/'), out int p)
                ? p : fallback;
        }
    }

    private static string FindRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Rochas.OpenCodeBridge.Test",
                    "Rochas.OpenCodeBridge.Test.csproj")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }
        dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "Rochas.OpenCodeBridge.Test",
                    "Rochas.OpenCodeBridge.Test.csproj")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName ?? "";
        }
        return Directory.GetCurrentDirectory();
    }

    private static string NowStamp()
        => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private static string StampOf(string outDir)
    {
        string name = Path.GetFileName(outDir);
        int dash = name.IndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : "";
    }

    private static string? Env(string key)
    {
        string? v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private static bool? EnvBool(string key)
    {
        string? v = Env(key);
        if (v is null) return null;
        return v is "1" or "true" or "True" or "yes" or "YES";
    }

    private static int ParseInt(string? text, int fallback)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : fallback;

    private static string Hash(string path)
    {
        if (!File.Exists(path)) return "";
        try
        {
            using var fs = File.OpenRead(path);
            byte[] h = System.Security.Cryptography.SHA256.HashData(fs);
            return Convert.ToHexString(h).ToLowerInvariant()[..16];
        }
        catch
        {
            return "";
        }
    }

    private static List<int> LevelsToRun(WarOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.Levels))
        {
            var list = new List<int>();
            foreach (string part in o.Levels.Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(part, out int v) && v is >= 1 and <= 4) list.Add(v);
            return list.Count > 0 ? list : new List<int> { 1, 2, 3, 4 };
        }
        var all = new List<int>();
        for (int i = o.From; i <= o.To; i++)
            if (i is >= 1 and <= 4) all.Add(i);
        return all.Count > 0 ? all : new List<int> { 1, 2, 3, 4 };
    }

    private static void LogGlobal(TextWriter? log, string message)
    {
        Console.WriteLine(message);
        log?.WriteLine(message);
    }

    private static int ControlPause(WarPaths p)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p.PausePath)!);
            File.WriteAllText(p.PausePath, $"{DateTimeOffset.Now:O}{Environment.NewLine}");
            Console.WriteLine($"PAUSA: {p.PausePath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL pausa: {ex.Message}");
            return 1;
        }
    }

    private static int ControlResume(WarPaths p)
    {
        try
        {
            if (File.Exists(p.PausePath)) File.Delete(p.PausePath);
            Console.WriteLine($"RETOMADA: {p.PausePath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL retomada: {ex.Message}");
            return 1;
        }
    }

    private static int PrintStatus(WarPaths p)
    {
        Console.WriteLine($"lock: {p.LockPath} -> {CircuitLock.DescribeOwner(p.LockPath)}");
        Console.WriteLine($"pausa: {p.PausePath} -> {(File.Exists(p.PausePath) ? "PAUSADO" : "livre")}");
        Console.WriteLine($"out: {p.OutBase}");
        Console.WriteLine($"bridge: {p.Bridge} upstream: {p.Upstream} q3: {p.BridgeQ3 ?? "n/d"}");
        Console.WriteLine($"binarios: test={Hash(p.TestDll)} bridge={Hash(p.BridgeDll)} web={Hash(p.WebTestDll)}");
        if (!Directory.Exists(p.OutBase))
        {
            Console.WriteLine("levels: sem artefatos");
            return 0;
        }
        foreach (string dir in Directory.GetDirectories(p.OutBase, "L*-*")
                     .OrderByDescending(d => d, StringComparer.Ordinal).Take(5))
        {
            var counts = SummaryCounts(Path.Combine(dir, "summary.tsv"));
            Console.WriteLine($"  {Path.GetFileName(dir)}: {counts}");
        }
        return 0;
    }

    private static string SummaryCounts(string summaryPath)
    {
        if (!File.Exists(summaryPath)) return "sem summary.tsv";
        int p = 0, f = 0, s = 0, st = 0;
        try
        {
            foreach (string line in File.ReadLines(summaryPath).Skip(1))
            {
                string[] cols = line.Split('\t');
                if (cols.Length < 2) continue;
                switch (cols[1])
                {
                    case "PASS": p++; break;
                    case "FAIL": f++; break;
                    case "SKIP": s++; break;
                    case "STALE": st++; break;
                }
            }
        }
        catch
        {
            return "summary ilegivel";
        }
        return $"PASS={p} FAIL={f} SKIP={s} STALE={st}";
    }

    private void PrintSteps()
    {
        switch (_level)
        {
            case 1:
                Console.WriteLine("Level 1 -> units (bridge), integracao (E-stream/E-review/E-iot/E-error/E-full-cycle/E-healthy) e topicos (E-senior/E-ent), bridge (default)");
                break;
            case 2:
                Console.WriteLine("Level 2 -> responses-tools, tools-full, belts build/orch azul+verde, bridge (default)");
                break;
            case 3:
                Console.WriteLine("Level 3 -> science-gpu, mission-tools, belts build/orch roxa, bridge (default)");
                break;
            case 4:
                Console.WriteLine("Level 4 -> mission-algo C# (hanoi,bin,btree,kmeans,sort), belts build/orch marrom+preta, frontend pipeline (E-front), webui-integracao-topicos");
                break;
            default:
                Console.WriteLine($"Level {_level} invalido");
                break;
        }
    }

    // ---------------------------------------------------------------------
    // Parse / usage
    // ---------------------------------------------------------------------
    private static WarOptions Parse(string[] args)
    {
        var o = new WarOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"faltou valor para {a}");
            switch (a)
            {
                case "--war-level": o.Level = ParseInt(Next(), 1); break;
                case "--war-all": o.All = true; break;
                case "--war-levels": o.Levels = Next(); o.All = true; break;
                case "--war-only": o.OnlySteps = Next(); break;
                case "--war-from": o.From = ParseInt(Next(), 1); o.All = true; break;
                case "--war-to": o.To = ParseInt(Next(), 4); o.All = true; break;
                case "--war-stamp": o.Stamp = Next(); break;
                case "--war-resume-stamp": o.ResumeStamp = Next(); o.All = true; break;
                case "--war-resume-level": o.ResumeLevel = ParseInt(Next(), 1); o.All = true; break;
                case "--war-gate-only": o.GateOnly = true; break;
                case "--war-list": o.List = true; break;
                case "--war-require-green": o.RequireGreen = true; break;
                case "--war-no-require-green": o.RequireGreen = false; break;
                case "--war-out": o.Out = Next(); break;
                case "--war-bridge": o.Bridge = Next(); break;
                case "--war-upstream": o.Upstream = Next(); break;
                case "--war-api-key": o.ApiKey = Next(); break;
                case "--war-bridge-q3": o.BridgeQ3 = Next(); break;
                case "--war-blog": o.Blog = Next(); break;
                case "--war-lock": o.Lock = Next(); break;
                case "--war-mode":
                case "--war-modes": o.Modes = Next(); break;
                case "--war-pause-file": o.PauseFile = Next(); break;
                case "--war-root": o.Root = Next(); break;
                case "--war-webdriver": o.WebDriver = true; break;
                case "--war-webdriver-url": o.WebDriverUrl = Next(); break;
                case "--war-headed": o.WebHeaded = true; break;
                case "--war-webui-belts": o.WebUiBelts = Next(); break;
                case "--war-step-timeout": o.StepTimeout = ParseInt(Next(), 7200); break;
                case "--war-retries": o.Retries = ParseInt(Next(), 1); break;
                case "--war-retry-delay": o.RetryDelay = ParseInt(Next(), 5); break;
                case "--war-retry-all": o.RetryAll = true; break;
                case "--war-rebuild": o.Rebuild = true; break;
                case "--war-no-build":
                case "--war-no-rebuild": o.NoBuild = true; break;
                case "--war-stop-on-fail": o.StopOnFirstFailure = true; break;
                case "--war-no-stop-on-fail":
                case "--war-continue": o.StopOnFirstFailure = false; break;
                case "--war-pause": o.Pause = true; break;
                case "--war-resume": o.Resume = true; break;
                case "--war-status": o.Status = true; break;
                case "--war-redeploy": o.Redeploy = true; break;
                case "--war-redeploy-stop": o.RedeployStop = Next(); break;
                case "--war-redeploy-stop-arg": o.RedeployStopArgs.Add(Next()); break;
                case "--war-redeploy-start": o.RedeployStart = Next(); break;
                case "--war-redeploy-start-arg": o.RedeployStartArgs.Add(Next()); break;
                case "--war-redeploy-timeout": o.RedeployTimeout = ParseInt(Next(), 120); break;
                case "--war-health-url": o.HealthUrl = Next(); break;
                case "--war-no-vision": o.NoVision = true; break;
                case "--war-vision": o.NoVision = false; break;
                case "--war-cpu": o.Cpu = true; break;
                case "--war-no-cpu": o.Cpu = false; break;
                case "--war-help": o.Help = true; break;
                default:
                    if (a.StartsWith("--war", StringComparison.Ordinal))
                        throw new ArgumentException($"arg desconhecido: {a}");
                    break;
            }
        }
        if (o.Level is < 1 or > 4) throw new ArgumentException("--war-level deve ser 1..4");
        if (o.From is < 1 or > 4) throw new ArgumentException("--war-from deve ser 1..4");
        if (o.To is < 1 or > 4) throw new ArgumentException("--war-to deve ser 1..4");
        if (o.From > o.To) throw new ArgumentException("--war-from maior que --war-to");
        if (o.Retries is < 1 or > 10) throw new ArgumentException("--war-retries deve ser 1..10");
        if (o.Pause && o.Resume) throw new ArgumentException("--war-pause e --war-resume sao exclusivos");
        if (o.Redeploy && string.IsNullOrWhiteSpace(o.RedeployStart))
            throw new ArgumentException("--war-redeploy exige --war-redeploy-start");
        return o;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
        WarTemplate (Rochas.OpenCodeBridge.Test) — template WAR do TestCircuitDriver

        EXECUCAO
          --war-level N            executa um level (1..4)
          --war-all                executa 1 -> 4 em sequencia
          --war-levels 1,2,3,4     levels explicitos
          --war-only NAME          roda so steps cujo nome contem NAME (virgula = lista)
          --war-modes bridge       modos (bridge|opencode), default "bridge"
          --war-from N --war-to N  faixa de levels
          --war-stamp STAMP        stamp do run (level unico)
          --war-resume-stamp S     retoma um stamp no --war-all
          --war-resume-level N     level do stamp retomado
          --war-gate-only          recalcula o gate sem executar steps
          --war-list               lista os steps do level
          --war-stop-on-fail       para no primeiro FAIL/STALE/BLOCKED (default)
          --war-no-stop-on-fail    segue mesmo com FAIL (mede placar completo)
          --war-require-green      gate vermelho derruba o exit (default)
          --war-no-require-green   nao derruba

        COMPILACAO / DEPLOY / RESTART
          --war-rebuild            forca dotnet build da solucao antes do level
          --war-no-build           nao compila (exige binarios preexistentes)
          --war-retries N          tentativas totais por step (1..10; default 1)
          --war-retry-delay SEC    espera entre tentativas (default 5)
          --war-retry-all          reinicia tambem FAIL de assertiva (nao so 124/137)
          --war-redeploy           liga stop/start/health do servico antes do preflight
          --war-redeploy-stop EXE  executavel de stop (opcional)
          --war-redeploy-stop-arg V   argumento do stop (repetivel)
          --war-redeploy-start EXE executavel de start (obrigatorio com --war-redeploy)
          --war-redeploy-start-arg V  argumento do start (repetivel)
          --war-redeploy-timeout S timeout do start/health (default 120)
          --war-health-url URL     health do redeploy (default BRIDGE/api/status)

        CONTROLE
          --war-pause              cria o marcador de pausa (entre steps)
          --war-resume             remove o marcador de pausa
          --war-status             mostra lock, pausa, binarios e ultimos levels
          --war-pause-file PATH    marcador alternativo (default OUT/pause)

        ALVOS / INFRA
          --war-out DIR            base dos artefatos (default logs/levels)
          --war-bridge URL         bridge alvo (default http://127.0.0.1:4124)
          --war-upstream URL       llama upstream (default http://127.0.0.1:4100)
          --war-api-key KEY        Bearer (default guest@123)
          --war-bridge-q3 URL      bridge da raia Q3 (WAR_CPU_BRIDGE)
          --war-blog PATH          log da bridge (default TMP/bridge-gpu.log)
          --war-lock PATH          lock (default TMP/opencode/war.lock)
          --war-step-timeout SEC   timeout por step (default 7200)
          --war-no-vision          pula visao (WAR_NO_VISION=1)
          --war-no-cpu             desliga science-cpu (LEVEL_CPU=0)
          --war-root DIR           raiz do repo (default: descoberta)
          --war-help               esta ajuda
        """);
    }
}
