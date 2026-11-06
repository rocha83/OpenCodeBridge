// ===========================================================================
//  Testes do nucleo generico (TestCircuitDriver): steps, gate, pausa,
//  retry/restart, build incremental, deploy e lock. Sem modelo/bridge.
// ===========================================================================

using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

internal static class UnitCircuitTests
{
    private static string NewRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "war-circuit-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class FakeTemplate : CircuitTemplate
    {
        private readonly CircuitStepSpec[] _steps;
        private bool _nextEmitted;
        public bool Started;
        public bool Ended;
        public int BuildCount;
        public int DeployCount;
        public CircuitStepSpec[] NextStepsAfter = Array.Empty<CircuitStepSpec>();
        public CircuitBuildSpec[]? Builds;
        public CircuitDeploymentSpec[]? Deployments;
        public CircuitGatePolicy? Policy;

        public FakeTemplate(params CircuitStepSpec[] steps) => _steps = steps;

        public override string Name => "fake";
        public override string Label => "teste";
        public override IEnumerable<CircuitStepSpec> Steps(TestCircuitDriver driver) => _steps;

        public override IReadOnlyList<CircuitBuildSpec> BuildSpecs(TestCircuitDriver driver)
            => Builds ?? base.BuildSpecs(driver);
        public override IReadOnlyList<CircuitDeploymentSpec> DeploymentSpecs(TestCircuitDriver driver)
            => Deployments ?? base.DeploymentSpecs(driver);
        public override IEnumerable<CircuitStepSpec> NextSteps(
            TestCircuitDriver driver, CircuitStepResult result)
        {
            if (_nextEmitted) return Array.Empty<CircuitStepSpec>();
            _nextEmitted = true;
            return NextStepsAfter;
        }
        public override CircuitGatePolicy GatePolicy(TestCircuitDriver driver)
            => Policy ?? base.GatePolicy(driver);
        public override void OnRunStart(TestCircuitDriver driver) => Started = true;
        public override void OnBuildCompleted(TestCircuitDriver driver, CircuitBuildResult result)
            => BuildCount++;
        public override void OnDeployCompleted(
            TestCircuitDriver driver, CircuitDeploymentResult result) => DeployCount++;
        public override void OnRunEnd(TestCircuitDriver driver, CircuitGateResult gate) => Ended = true;
    }

    private static TestCircuitDriver NewDriver(
        string root, CircuitTemplate? template = null, int maxParallel = 1)
    {
        return new TestCircuitDriver(new CircuitOptions(
            Root: root,
            RunDirectory: Path.Combine(root, "run"),
            LockPath: Path.Combine(root, "lock"),
            PausePath: Path.Combine(root, "pause"),
            DefaultTimeoutSeconds: 30,
            RequireGreen: true,
            AcquireLock: false,
            MaxParallelSteps: maxParallel), template ?? new FakeTemplate());
    }

    private static CircuitCommand Dotnet(params string[] args)
        => new(Execution.Resolve("dotnet"), args, Path.GetTempPath(), null);

    private static CircuitStepSpec Step(string name, params string[] args)
        => new(name, Dotnet(args), 30);

    private static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch { }
    }

    public static Task StepAndGate()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            var result = circuit.RunStepAsync(Step("ok", "--version")).GetAwaiter().GetResult();
            TestContext.Check(result.Verdict == CircuitVerdict.Pass,
                $"verdict={result.Verdict} rc={result.ExitCode}");
            var gate = circuit.Gate();
            TestContext.Check(gate.Steps == 1 && gate.Pass == 1 && gate.Green,
                $"gate steps={gate.Steps} pass={gate.Pass} green={gate.Green}");
            string summary = File.ReadAllText(Path.Combine(circuit.RunDirectory, "summary.tsv"));
            TestContext.Check(summary.Contains("ok\tPASS\t", StringComparison.Ordinal),
                "summary.tsv sem a linha PASS do step");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task FailGate()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            var result = circuit.RunStepAsync(Step("bad", "--definitely-invalid-flag"))
                .GetAwaiter().GetResult();
            TestContext.Check(result.Verdict == CircuitVerdict.Fail,
                $"verdict={result.Verdict} rc={result.ExitCode}");
            var gate = circuit.Gate();
            TestContext.Check(gate.Fail == 1 && !gate.Green,
                $"gate fail={gate.Fail} green={gate.Green}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task ResumeSkipsDone()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            var spec = Step("once", "--version");
            var first = circuit.RunStepAsync(spec).GetAwaiter().GetResult();
            var second = circuit.RunStepAsync(spec).GetAwaiter().GetResult();
            TestContext.Check(first.Verdict == CircuitVerdict.Pass, $"1o={first.Verdict}");
            TestContext.Check(second.Skipped && second.Verdict == CircuitVerdict.Skip,
                $"2o skipped={second.Skipped} verdict={second.Verdict}");
            int rows = File.ReadLines(Path.Combine(circuit.RunDirectory, "summary.tsv"))
                .Count(l => l.Length > 0) - 1;
            TestContext.Check(rows == 1, $"summary deveria ter 1 linha, tem {rows}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Bateria real de 3 steps: falha no meio PARA tudo (o 3o nao executa),
    /// FAIL nao vira .done e o proximo run refaz do ponto que parou.
    /// </summary>
    public static Task BatteryStopsOnFail()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            var results = circuit.RunStepsAsync(new[]
            {
                Step("b1-ok", "--version"),
                Step("b2-fail", "--definitely-invalid-flag"),
                Step("b3-depois", "--version")
            }).GetAwaiter().GetResult();

            TestContext.Check(results.Count == 3, $"results={results.Count}");
            TestContext.Check(results[0].Verdict == CircuitVerdict.Pass, $"b1={results[0].Verdict}");
            TestContext.Check(results[1].Verdict == CircuitVerdict.Fail, $"b2={results[1].Verdict}");
            TestContext.Check(results[2].Skipped && results[2].Verdict == CircuitVerdict.Skip,
                $"b3={results[2].Verdict} skipped={results[2].Skipped}");
            TestContext.Check(results[2].Cause.Contains("nao executado", StringComparison.Ordinal),
                $"causa b3='{results[2].Cause}'");

            string state = Path.Combine(circuit.RunDirectory, "state");
            TestContext.Check(File.Exists(Path.Combine(state, "b1-ok.done")),
                "PASS deveria gravar .done");
            TestContext.Check(!File.Exists(Path.Combine(state, "b2-fail.done")),
                "FAIL nao pode gravar .done (senao o resume pula a falha)");
            TestContext.Check(!File.Exists(Path.Combine(state, "b3-depois.done")),
                "step nao executado nao pode ter .done");

            var gate = circuit.Gate();
            TestContext.Check(gate.Fail == 1 && gate.Skip >= 1 && !gate.Green,
                $"gate fail={gate.Fail} skip={gate.Skip} green={gate.Green}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// Segundo run no MESMO run dir (como --war-resume-stamp): PASS anterior e
    /// pulado, o step que falhou reexecuta e a bateria segue ate o fim.
    /// </summary>
    public static Task BatteryResumesAfterFail()
    {
        string root = NewRoot();
        try
        {
            using (var first = NewDriver(root))
            {
                var r = first.RunStepsAsync(new[]
                {
                    Step("r1", "--version"),
                    Step("r2", "--definitely-invalid-flag"),
                    Step("r3", "--version")
                }).GetAwaiter().GetResult();
                TestContext.Check(r[0].Verdict == CircuitVerdict.Pass, $"1a={r[0].Verdict}");
                TestContext.Check(r[1].Verdict == CircuitVerdict.Fail, $"1b={r[1].Verdict}");
                TestContext.Check(r[2].Skipped, "1c deveria ser SKIP (parou na falha)");
            }

            using (var second = NewDriver(root))
            {
                var r = second.RunStepsAsync(new[]
                {
                    Step("r1", "--version"),
                    Step("r2", "--version"),
                    Step("r3", "--version")
                }).GetAwaiter().GetResult();
                TestContext.Check(r[0].Skipped, "r1 deveria ser SKIP (PASS do run anterior)");
                TestContext.Check(r[1].Verdict == CircuitVerdict.Pass, $"2b={r[1].Verdict}");
                TestContext.Check(r[2].Verdict == CircuitVerdict.Pass, $"2c={r[2].Verdict}");
                var gate = second.Gate();
                TestContext.Check(gate.Green && gate.Pass == 3 && gate.Fail == 0,
                    $"gate pass={gate.Pass} fail={gate.Fail} green={gate.Green}");
            }
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>
    /// step_start chega na bridge como RUN ANTES do veredito (3 steps reais):
    /// RUN(p1) -> PASS(p1) -> RUN(p2) -> ... Sem isso o painel Grafana nao tem
    /// linha do step em execucao — ele so aparece quando ja acabou.
    /// </summary>
    public static Task PublishesRunBeforeVerdict()
    {
        string root = NewRoot();
        var received = new List<JsonObject>();
        string prevUrl = Environment.GetEnvironmentVariable("WAR_EVENT_URL") ?? "";
        string prevKey = Environment.GetEnvironmentVariable("WAR_EVENT_KEY") ?? "";
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{FreePort()}/step/");
        listener.Start();
        var collector = Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    var ctx = listener.GetContext();
                    string body;
                    using (var sr = new StreamReader(ctx.Request.InputStream)) body = sr.ReadToEnd();
                    var node = JsonNode.Parse(body)?.AsObject();
                    if (node != null) lock (received) received.Add(node);
                    byte[] ack = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");
                    ctx.Response.OutputStream.Write(ack, 0, ack.Length);
                    ctx.Response.Close();
                }
            }
            catch { /* listener.Stop() encerra o loop */ }
        });

        Environment.SetEnvironmentVariable("WAR_EVENT_URL", listener.Prefixes.First());
        Environment.SetEnvironmentVariable("WAR_EVENT_KEY", "");
        try
        {
            using var circuit = NewDriver(root);
            var results = circuit.RunStepsAsync(new[]
            {
                new CircuitStepSpec("p1", Dotnet("--version"), 30, Mode: "bridge"),
                new CircuitStepSpec("p2", Dotnet("--version"), 30, Mode: "bridge"),
                new CircuitStepSpec("p3", Dotnet("--version"), 30, Mode: "bridge")
            }).GetAwaiter().GetResult();
            TestContext.Check(results.All(r => r.Verdict == CircuitVerdict.Pass),
                $"os 3 steps deveriam passar (vi: {string.Join(",", results.Select(r => r.Verdict))})");
            bool chegou = System.Threading.SpinWait.SpinUntil(
                () => { lock (received) return received.Count >= 6; }, TimeSpan.FromSeconds(10));
            TestContext.Check(chegou, "driver nao enviou os 6 eventos (3 RUN + 3 veredito)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("WAR_EVENT_URL", prevUrl);
            Environment.SetEnvironmentVariable("WAR_EVENT_KEY", prevKey);
            try { listener.Stop(); } catch { }
        }

        lock (received)
        {
            TestContext.Check(received.Count == 6,
                $"6 eventos esperados, vieram {received.Count}");
            for (int i = 0; i < 3; i++)
            {
                string nome = "p" + (i + 1);
                var run = received[2 * i];
                var fim = received[2 * i + 1];
                TestContext.Check(run["t"]?.GetValue<string>() == "step_start",
                    $"{nome}: 1o evento deveria ser step_start (veio {run["t"]})");
                TestContext.Check(run["verdict"]?.GetValue<string>() == "RUN",
                    $"{nome}: 1o evento deveria ser verdict RUN (veio {run["verdict"]})");
                TestContext.Check(fim["t"]?.GetValue<string>() == "step_end",
                    $"{nome}: 2o evento deveria ser step_end (veio {fim["t"]})");
                TestContext.Check(fim["verdict"]?.GetValue<string>() == "PASS",
                    $"{nome}: veredito deveria ser PASS (veio {fim["verdict"]})");
                TestContext.Check(run["step"]?.GetValue<string>() == nome
                                  && fim["step"]?.GetValue<string>() == nome,
                    $"{nome}: RUN e veredito devem ser do mesmo step");
                // O painel Grafana le ?war=1, que descarta row sem mode.
                TestContext.Check(run["mode"]?.GetValue<string>() == "bridge"
                                  && fim["mode"]?.GetValue<string>() == "bridge",
                    $"{nome}: RUN/veredito precisam de mode=bridge (filtro ?war=1)");
            }
        }
        return Task.CompletedTask;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static Task RestartRetries()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            var spec = Step("retry", "--definitely-invalid-flag") with
            {
                Restart = new CircuitRestartPolicy(2, 0, (_, _) => true)
            };
            var result = circuit.RunStepAsync(spec).GetAwaiter().GetResult();
            TestContext.Check(result.Verdict == CircuitVerdict.Fail, $"verdict={result.Verdict}");
            TestContext.Check(result.Attempt == 2, $"attempt={result.Attempt} (esperado 2)");
            int rows = File.ReadLines(Path.Combine(circuit.RunDirectory, "summary.tsv"))
                .Count(l => l.Length > 0) - 1;
            TestContext.Check(rows == 1, $"restart nao deve duplicar summary (rows={rows})");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task PauseResumes()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            circuit.Pause();
            var task = circuit.RunStepAsync(Step("paused", "--version"));
            Thread.Sleep(400);
            TestContext.Check(!task.IsCompleted, "step iniciou com marcador de pausa presente");
            circuit.Resume();
            var result = task.GetAwaiter().GetResult();
            TestContext.Check(result.Verdict == CircuitVerdict.Pass, $"verdict={result.Verdict}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task LockExclusive()
    {
        string root = NewRoot();
        try
        {
            string path = Path.Combine(root, "runner.lock");
            var first = CircuitLock.TryAcquire(path);
            TestContext.Check(first is not null, "primeiro lock deveria adquirir");
            try
            {
                TestContext.Check(CircuitLock.TryAcquire(path) is null,
                    "segundo lock deveria ser BLOCKED");
            }
            finally
            {
                first!.Dispose();
            }
            using var again = CircuitLock.TryAcquire(path);
            TestContext.Check(again is not null, "lock deveria ser reutilizavel apos dispose");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task BuildIncremental()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            string input = Path.Combine(src, "a.cs");
            File.WriteAllText(input, "internal class A { }");
            string output = Path.Combine(root, "out.dll");
            File.WriteAllText(output, "binario");
            File.SetLastWriteTimeUtc(input, DateTime.UtcNow.AddMinutes(-5));
            File.SetLastWriteTimeUtc(output, DateTime.UtcNow);

            var spec = new CircuitBuildSpec(
                "build", Dotnet("--version"), new[] { output }, new[] { src }, 30);
            var skipped = circuit.RecompileAsync(spec).GetAwaiter().GetResult();
            TestContext.Check(skipped.Ok && !skipped.Built,
                $"build deveria pular: ok={skipped.Ok} built={skipped.Built}");

            var forced = spec with { Force = true };
            var built = circuit.RecompileAsync(forced).GetAwaiter().GetResult();
            TestContext.Check(built.Ok && built.Built,
                $"build forcado: ok={built.Ok} built={built.Built} rc={built.ExitCode}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task DeployNotConfigured()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            var result = circuit.RedeployAsync(
                new CircuitDeploymentSpec("deploy", null, null, null))
                .GetAwaiter().GetResult();
            TestContext.Check(result.Ok && result.Skipped,
                $"deploy vazio: ok={result.Ok} skipped={result.Skipped}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task DeployHealth()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            int checks = 0;
            var spec = new CircuitDeploymentSpec(
                "deploy",
                Stop: null,
                Start: Dotnet("--version"),
                Healthy: _ => Task.FromResult(++checks >= 2),
                TimeoutSeconds: 5,
                IntervalMs: 10);
            var result = circuit.RedeployAsync(spec).GetAwaiter().GetResult();
            TestContext.Check(result.Ok, $"deploy: ok={result.Ok} rc={result.ExitCode} cause={result.Cause}");
            TestContext.Check(checks >= 2, $"health deveria ser consultado 2x, foi {checks}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task TemplateRunsSteps()
    {
        string root = NewRoot();
        try
        {
            var template = new FakeTemplate(Step("tpl", "--version"));
            using var driver = new TestCircuitDriver(new CircuitOptions(
                Root: root,
                RunDirectory: Path.Combine(root, "run"),
                LockPath: Path.Combine(root, "run.lock"),
                PausePath: Path.Combine(root, "pause"),
                DefaultTimeoutSeconds: 30,
                RequireGreen: true,
                AcquireLock: true), template);

            var result = driver.RunAsync().GetAwaiter().GetResult();
            TestContext.Check(!result.Blocked, $"template bloqueou: {result.Cause}");
            TestContext.Check(result.ExitCode == 0 && result.Gate is { Green: true },
                $"template exit={result.ExitCode} green={result.Gate?.Green}");
            TestContext.Check(template.Started && template.Ended,
                $"hooks: start={template.Started} end={template.Ended}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task ConditionalSteps()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            var skipped = Step("skip", "--version") with { When = _ => false };
            var ok = Step("ok", "--version") with { When = _ => true };
            var results = circuit.RunStepsAsync(new[] { skipped, ok })
                .GetAwaiter().GetResult();
            var s = results.Single(r => r.Name == "skip");
            var p = results.Single(r => r.Name == "ok");
            TestContext.Check(s.Skipped && s.Verdict == CircuitVerdict.Skip,
                $"skip: skipped={s.Skipped} verdict={s.Verdict}");
            TestContext.Check(p.Verdict == CircuitVerdict.Pass, $"ok: {p.Verdict}");
            var gate = circuit.Gate();
            TestContext.Check(gate.Steps == 1 && gate.Pass == 1 && gate.Green,
                $"gate steps={gate.Steps} pass={gate.Pass} green={gate.Green}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task DependencyOrder()
    {
        string root = NewRoot();
        try
        {
            using var circuit = NewDriver(root);
            var order = new List<string>();
            var a = new CircuitStepSpec("a", Dotnet("--version"), 30,
                Verdict: _ => { order.Add("a"); return CircuitStepOutcome.Pass; });
            var b = new CircuitStepSpec("b", Dotnet("--version"), 30,
                Verdict: _ => { order.Add("b"); return CircuitStepOutcome.Pass; },
                DependsOn: new[] { "a" });
            var results = circuit.RunStepsAsync(new[] { b, a }).GetAwaiter().GetResult();
            TestContext.Check(results.All(r => r.Verdict == CircuitVerdict.Pass),
                "a/b deveriam passar");
            TestContext.Check(string.Join("", order) == "ab",
                $"ordem observada: {string.Join("", order)} (esperado ab)");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task ParallelSteps()
    {
        string root = NewRoot();
        try
        {
            var template = new FakeTemplate(
                Step("p1", "--version"),
                Step("p2", "--version"));
            using var circuit = NewDriver(root, template, maxParallel: 2);
            var result = circuit.RunAsync().GetAwaiter().GetResult();
            TestContext.Check(!result.Blocked, $"bloqueou: {result.Cause}");
            TestContext.Check(result.Gate is { Steps: 2, Pass: 2, Green: true },
                $"gate={result.Gate}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task DynamicNextSteps()
    {
        string root = NewRoot();
        try
        {
            var template = new FakeTemplate(Step("first", "--version"))
            {
                NextStepsAfter = new[] { Step("next", "--version") }
            };
            using var circuit = NewDriver(root, template);
            var result = circuit.RunAsync().GetAwaiter().GetResult();
            TestContext.Check(result.ExitCode == 0 && result.Gate is { Pass: 2, Green: true },
                $"exit={result.ExitCode} gate={result.Gate}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task CustomGatePolicy()
    {
        string root = NewRoot();
        try
        {
            var template = new FakeTemplate(Step("one", "--version"))
            {
                Policy = new CircuitGatePolicy("pass>=2", g => g.Pass >= 2)
            };
            using var circuit = NewDriver(root, template);
            var result = circuit.RunAsync().GetAwaiter().GetResult();
            TestContext.Check(result.Gate is { Green: false, Pass: 1 },
                $"gate={result.Gate}");
            TestContext.Check(result.ExitCode == 1, $"exit={result.ExitCode}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }

    public static Task MultiBuildAndDeploy()
    {
        string root = NewRoot();
        try
        {
            var template = new FakeTemplate(Step("ok", "--version"))
            {
                Builds = new[]
                {
                    new CircuitBuildSpec("b1", Dotnet("--version"),
                        Array.Empty<string>(), Array.Empty<string>(), 30, true),
                    new CircuitBuildSpec("b2", Dotnet("--version"),
                        Array.Empty<string>(), Array.Empty<string>(), 30, true)
                },
                Deployments = new[]
                {
                    new CircuitDeploymentSpec("d1", null, null, null),
                    new CircuitDeploymentSpec("d2", null, null, null)
                }
            };
            using var circuit = NewDriver(root, template);
            var result = circuit.RunAsync().GetAwaiter().GetResult();
            TestContext.Check(!result.Blocked, $"bloqueou: {result.Cause}");
            TestContext.Check(template.BuildCount == 2, $"builds={template.BuildCount}");
            TestContext.Check(template.DeployCount == 2, $"deploys={template.DeployCount}");
            TestContext.Check(result.ExitCode == 0 && result.Gate is { Green: true },
                $"exit={result.ExitCode} gate={result.Gate}");
            return Task.CompletedTask;
        }
        finally
        {
            Cleanup(root);
        }
    }
}
