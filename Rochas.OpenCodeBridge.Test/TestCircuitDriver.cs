// ===========================================================================
//  TestCircuitDriver — driver generico e portavel de circuitos de teste.
//
//  O TestCircuitDriver NAO conhece WAR, levels, bridge, llama, Q4/Q3 ou opencode.
//  Ele executa um CircuitTemplate (a "receita" do circuito) e controla:
//    - lock unico de runner (FileShare.None, Windows/Linux);
//    - pausa/retomada por arquivo marcador, em pontos seguros;
//    - N builds/ redeploys com health check;
//    - steps em DAG (DependsOn), condicionais (When), dinamicos (NextSteps)
//      e paralelismo limitado (MaxParallelSteps);
//    - timeout, tentativas e restart por step;
//    - artefatos: steps/*.log, state/*.done|attempt, summary.tsv,
//      summary.txt e events.jsonl;
//    - gate final com PASS/FAIL/SKIP/STALE e politica do template.
//  Execucao e local; remote fica para um adapter futuro, fora deste nucleo.
//
//  A WAR e apenas um template (WarTemplate.cs): informa build, preflight,
//  steps, parser de veredito e gate. Nada aqui depende de shell, /proc,
//  flock(2) ou nvidia-smi.
// ===========================================================================

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Veredito de um step ou do gate.</summary>
internal enum CircuitVerdict
{
    Pass,
    Fail,
    Skip,
    Stale,
    Blocked
}

/// <summary>Resultado semantico de um step (veredito + causa objetiva).</summary>
internal sealed record CircuitStepOutcome(CircuitVerdict Verdict, string Cause = "")
{
    public static CircuitStepOutcome Pass { get; } = new(CircuitVerdict.Pass);
    public static CircuitStepOutcome Fail(string cause) => new(CircuitVerdict.Fail, cause);
    public static CircuitStepOutcome Skip(string cause = "") => new(CircuitVerdict.Skip, cause);
    public static CircuitStepOutcome Stale(string cause) => new(CircuitVerdict.Stale, cause);
}

/// <summary>Comando externo portavel (executavel + args + ambiente).</summary>
internal sealed record CircuitCommand(
    string FileName,
    IReadOnlyList<string> Args,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// Politica de restart do processo de teste. MaxAttempts e o total de
/// tentativas (1 = sem restart). Sem ShouldRetry, so reinicia timeout/crash
/// (exit 124/137), nunca um FAIL de assertiva.
/// </summary>
internal sealed record CircuitRestartPolicy(
    int MaxAttempts = 1,
    int DelaySeconds = 0,
    Func<CircuitStepRun, CircuitStepOutcome, bool>? ShouldRetry = null);

/// <summary>Especificacao de um step do circuito.</summary>
internal sealed record CircuitStepSpec(
    string Name,
    CircuitCommand Command,
    int TimeoutSeconds,
    string Mode = "",
    string? RunTag = null,
    string? FollowLogPath = null,
    string? FollowLogOutputPath = null,
    string? ProbeUrl = null,
    string? ProbeApiKey = null,
    IReadOnlyList<string>? ProbeLogs = null,
    CircuitRestartPolicy? Restart = null,
    Func<CircuitStepRun, CircuitStepOutcome>? Verdict = null,
    IReadOnlyList<string>? DependsOn = null,
    Func<TestCircuitDriver, bool>? When = null);

/// <summary>Fatos de uma execucao de step, entregues ao parser de veredito.</summary>
internal sealed record CircuitStepRun(
    string Name,
    int Attempt,
    int ExitCode,
    long Seconds,
    string LogPath,
    string Output,
    string Mode,
    IReadOnlyList<string> Args);

/// <summary>Resultado final de um step (apos restart, se houver).</summary>
internal sealed record CircuitStepResult(
    string Name,
    CircuitVerdict Verdict,
    long Seconds,
    int ExitCode,
    string Cause,
    int Attempt,
    string LogPath,
    bool Skipped = false);

/// <summary>Especificacao de recompilacao incremental.</summary>
internal sealed record CircuitBuildSpec(
    string Name,
    CircuitCommand Command,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> InputRoots,
    int TimeoutSeconds = 1800,
    bool Force = false);

internal sealed record CircuitBuildResult(
    bool Ok,
    bool Built,
    long Seconds,
    int ExitCode,
    string LogPath,
    string Cause,
    string? Name = null);

/// <summary>
/// Redeploy generico: para o servico (opcional), sobe de novo e espera o
/// health check. Sem Stop/Start e um no-op explicito (Skipped).
/// </summary>
internal sealed record CircuitDeploymentSpec(
    string Name,
    CircuitCommand? Stop,
    CircuitCommand? Start,
    Func<CancellationToken, Task<bool>>? Healthy,
    int TimeoutSeconds = 120,
    int IntervalMs = 1000);

internal sealed record CircuitDeploymentResult(
    bool Ok,
    bool Skipped,
    long Seconds,
    int ExitCode,
    string Cause,
    string? Name = null);

/// <summary>Opcoes do circuito; caminhos sempre absolutos ou relativos a Root.</summary>
internal sealed record CircuitOptions(
    string Root,
    string RunDirectory,
    string? LockPath = null,
    string? PausePath = null,
    int DefaultTimeoutSeconds = 7200,
    bool RequireGreen = true,
    bool AcquireLock = true,
    Action<string>? LogSink = null,
    int MaxParallelSteps = 1,
    bool StopOnFirstFailure = true);

internal sealed record CircuitGateResult(
    int Steps,
    int Pass,
    int Fail,
    int Skip,
    int Stale,
    long Seconds,
    bool Green);

/// <summary>
/// Politica de gate do template. O driver calcula os contadores e pergunta
/// ao template se aquele resultado e verde; o default exige pelo menos um
/// PASS e zero FAIL/STALE. Pipelines com outro criterio (ex.: cobertura,
/// numero minimo de PASS, ausencia de SKIP) sobrescrevem GatePolicy.
/// </summary>
internal sealed record CircuitGatePolicy(
    string Name,
    Func<CircuitGateResult, bool> IsGreen)
{
    public static CircuitGatePolicy Default { get; } = new(
        "pass>0 && fail==0 && stale==0",
        gate => gate.Pass > 0 && gate.Fail == 0 && gate.Stale == 0);
}

/// <summary>Resultado de uma corrida completa do TestCircuitDriver.</summary>
internal sealed record CircuitRunResult(
    bool Blocked,
    string Cause,
    CircuitGateResult? Gate,
    int ExitCode);

/// <summary>
/// Contrato do circuito concreto. O TestCircuitDriver chama estes hooks na ordem:
/// OnRunStart -> BuildSpecs -> OnBuildCompleted (por build) -> DeploymentSpecs ->
/// OnDeployCompleted (por deploy) -> PreflightAsync -> Steps/DAG ->
/// NextSteps (dinamico) -> Gate -> OnRunEnd.
///
/// BuildSpec/DeploymentSpec sao o caso simples de um servico; BuildSpecs/
/// DeploymentSpecs permitem N servicos. Steps pode ter DependsOn e When, e
/// NextSteps permite ramificacao depois do resultado de um step.
/// </summary>
internal abstract class CircuitTemplate
{
    public abstract string Name { get; }
    public abstract string Label { get; }

    public virtual CircuitBuildSpec? BuildSpec(TestCircuitDriver driver) => null;
    public virtual CircuitDeploymentSpec? DeploymentSpec(TestCircuitDriver driver) => null;
    public virtual Task<bool> PreflightAsync(TestCircuitDriver driver, CancellationToken ct)
        => Task.FromResult(true);
    public abstract IEnumerable<CircuitStepSpec> Steps(TestCircuitDriver driver);

    /// <summary>Builds do pipeline; default encapsula o unico BuildSpec.</summary>
    public virtual IReadOnlyList<CircuitBuildSpec> BuildSpecs(TestCircuitDriver driver)
    {
        var spec = BuildSpec(driver);
        return spec is null ? Array.Empty<CircuitBuildSpec>() : new[] { spec };
    }

    /// <summary>Deploys do pipeline; default encapsula o unico DeploymentSpec.</summary>
    public virtual IReadOnlyList<CircuitDeploymentSpec> DeploymentSpecs(TestCircuitDriver driver)
    {
        var spec = DeploymentSpec(driver);
        return spec is null ? Array.Empty<CircuitDeploymentSpec>() : new[] { spec };
    }

    /// <summary>Passos adicionados dinamicamente depois de um resultado (branch).</summary>
    public virtual IEnumerable<CircuitStepSpec> NextSteps(
        TestCircuitDriver driver, CircuitStepResult result)
        => Array.Empty<CircuitStepSpec>();

    /// <summary>Politica de gate; default = PASS>0, FAIL=0, STALE=0.</summary>
    public virtual CircuitGatePolicy GatePolicy(TestCircuitDriver driver)
        => CircuitGatePolicy.Default;

    public virtual void OnRunStart(TestCircuitDriver driver) { }
    public virtual void OnBuildCompleted(TestCircuitDriver driver, CircuitBuildResult result) { }
    public virtual void OnDeployCompleted(TestCircuitDriver driver, CircuitDeploymentResult result) { }
    public virtual void OnRunEnd(TestCircuitDriver driver, CircuitGateResult gate) { }
}

/// <summary>
/// Executor generico do circuito: aplica o template, controla execucao,
/// pausa, steps, recompilacao, redeploy, restart e gate.
/// </summary>
internal sealed class TestCircuitDriver : IDisposable
{
    private static readonly TimeSpan PausePoll = TimeSpan.FromMilliseconds(500);

    private readonly CircuitOptions _o;
    private readonly CircuitTemplate _template;
    private readonly string _stepsDir;
    private readonly string _stateDir;
    private readonly string _summaryPath;
    private readonly string _eventsPath;
    private readonly string _summaryTxtPath;
    private readonly string _pausePath;
    private readonly object _sync = new();
    private StreamWriter? _summaryWriter;
    private CircuitLock? _lock;
    private bool _disposed;

    public TestCircuitDriver(CircuitOptions options, CircuitTemplate template)
    {
        _o = options;
        _template = template;
        _stepsDir = Path.Combine(_o.RunDirectory, "steps");
        _stateDir = Path.Combine(_o.RunDirectory, "state");
        _summaryPath = Path.Combine(_o.RunDirectory, "summary.tsv");
        _eventsPath = Path.Combine(_o.RunDirectory, "events.jsonl");
        _summaryTxtPath = Path.Combine(_o.RunDirectory, "summary.txt");
        _pausePath = _o.PausePath ?? Path.Combine(_o.RunDirectory, "pause");

        Directory.CreateDirectory(_stepsDir);
        Directory.CreateDirectory(_stateDir);
        if (!File.Exists(_summaryPath))
            File.WriteAllText(_summaryPath, "step\tverdict\tsecs\trc\tcause\n");
        if (!File.Exists(_eventsPath)) File.WriteAllText(_eventsPath, "");
        _summaryWriter = new StreamWriter(_summaryTxtPath, append: true) { AutoFlush = true };
    }

    public CircuitOptions Options => _o;
    public CircuitTemplate Template => _template;
    public string RunDirectory => _o.RunDirectory;
    public string StepsDirectory => _stepsDir;
    public string StateDirectory => _stateDir;
    public string PausePath => _pausePath;
    public bool IsPaused => File.Exists(_pausePath);

    /// <summary>
    /// Executa a receita do template. Exit 0 = verde, 1 = gate vermelho,
    /// 3 = BLOCKED (lock/build/redeploy/preflight). Excecao sobe para o CLI.
    /// </summary>
    public async Task<CircuitRunResult> RunAsync(CancellationToken ct = default)
    {
        string lockPath = _o.LockPath
            ?? Path.Combine(Path.GetTempPath(), "opencode", "circuit.lock");
        if (_o.AcquireLock)
        {
            _lock = CircuitLock.TryAcquire(lockPath);
            if (_lock is null)
            {
                Log($"BLOCKED: outro runner ativo ({lockPath}) — {CircuitLock.DescribeOwner(lockPath)}");
                return new CircuitRunResult(true, "outro runner ativo", null, 3);
            }
        }

        try
        {
            await WaitIfPausedAsync(ct).ConfigureAwait(false);
            _template.OnRunStart(this);

            foreach (var build in _template.BuildSpecs(this))
            {
                await WaitIfPausedAsync(ct).ConfigureAwait(false);
                var buildResult = await RecompileAsync(build, ct).ConfigureAwait(false);
                _template.OnBuildCompleted(this, buildResult);
                if (!buildResult.Ok) return Blocked("build:" + build.Name, ct);
            }

            foreach (var deployment in _template.DeploymentSpecs(this))
            {
                await WaitIfPausedAsync(ct).ConfigureAwait(false);
                var deployResult = await RedeployAsync(deployment, ct).ConfigureAwait(false);
                _template.OnDeployCompleted(this, deployResult);
                if (!deployResult.Ok) return Blocked("redeploy:" + deployment.Name, ct);
            }

            await WaitIfPausedAsync(ct).ConfigureAwait(false);
            if (!await _template.PreflightAsync(this, ct).ConfigureAwait(false))
                return Blocked("preflight", ct);

            await RunPlanAsync(_template.Steps(this), ct).ConfigureAwait(false);

            var gate = Gate();
            _template.OnRunEnd(this, gate);
            return new CircuitRunResult(false, "", gate,
                _o.RequireGreen && !gate.Green ? 1 : 0);
        }
        finally
        {
            _lock?.Dispose();
            _lock = null;
        }
    }

    private CircuitRunResult Blocked(string cause, CancellationToken ct)
    {
        Publish(new JsonObject { ["t"] = "circuit_blocked", ["cause"] = cause });
        Log($"BLOCKED: {cause}");
        var gate = Gate();
        _template.OnRunEnd(this, gate);
        return new CircuitRunResult(true, cause, gate, 3);
    }

    // ------------------------------------------------------------------
    // Log / eventos
    // ------------------------------------------------------------------
    public void Log(string message)
    {
        lock (_sync)
        {
            Console.WriteLine(message);
            _summaryWriter?.WriteLine(message);
            _o.LogSink?.Invoke(message);
        }
    }

    /// <summary>Publica um fato em events.jsonl; nunca derruba o driver.</summary>
    public void Publish(JsonObject ev)
    {
        try
        {
            if (!string.IsNullOrEmpty(_eventsPath))
            {
                lock (_sync)
                    File.AppendAllText(_eventsPath, ev.ToJsonString() + Environment.NewLine);
            }
        }
        catch
        {
            // Evento e diagnostico: falha de disco nao pode parar o circuito.
        }
    }

    // ------------------------------------------------------------------
    // Pausa / retomada
    // ------------------------------------------------------------------
    /// <summary>Cria o marcador de pausa (seguro: vale antes do proximo step).</summary>
    public void Pause()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pausePath)!);
            File.WriteAllText(_pausePath,
                $"{DateTimeOffset.Now:O}{Environment.NewLine}");
            Publish(new JsonObject { ["t"] = "pause_request", ["path"] = _pausePath });
            Log($"PAUSA solicitada: {_pausePath}");
        }
        catch (Exception ex)
        {
            Log($"ERRO ao pausar: {ex.Message}");
        }
    }

    /// <summary>Remove o marcador de pausa; o proximo poll retoma o circuito.</summary>
    public void Resume()
    {
        try
        {
            if (File.Exists(_pausePath)) File.Delete(_pausePath);
            Publish(new JsonObject { ["t"] = "resume_request", ["path"] = _pausePath });
            Log($"PAUSA removida: {_pausePath}");
        }
        catch (Exception ex)
        {
            Log($"ERRO ao retomar: {ex.Message}");
        }
    }

    /// <summary>
    /// Bloqueia em ponto seguro enquanto o marcador existir. Nao suspende um
    /// processo ja iniciado: a pausa entra antes do proximo build/step/tentativa.
    /// </summary>
    public async Task WaitIfPausedAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_pausePath)) return;
        Log($"PAUSA: aguardando remocao de {_pausePath}");
        Publish(new JsonObject { ["t"] = "pause_enter", ["path"] = _pausePath });
        while (File.Exists(_pausePath))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PausePoll, ct).ConfigureAwait(false);
        }
        Publish(new JsonObject { ["t"] = "pause_exit", ["path"] = _pausePath });
        Log("RETOMANDO: pausa removida");
    }

    // ------------------------------------------------------------------
    // Steps
    // ------------------------------------------------------------------
    public async Task<CircuitStepResult> RunStepAsync(
        CircuitStepSpec step, CancellationToken ct = default)
    {
        string donePath = Path.Combine(_stateDir, step.Name + ".done");
        if (File.Exists(donePath) && DoneIsSkip(donePath))
        {
            Log($"SKIP {step.Name} (done)");
            return new CircuitStepResult(step.Name, CircuitVerdict.Skip, 0, 0, "", 0,
                Path.Combine(_stepsDir, step.Name + ".test.log"), Skipped: true);
        }

        if (step.When is not null)
        {
            bool enabled;
            try
            {
                enabled = step.When(this);
            }
            catch (Exception ex)
            {
                string cause = "condicao: " + ex.Message;
                var failed = new CircuitStepResult(
                    step.Name, CircuitVerdict.Fail, 0, -1, cause, 0,
                    Path.Combine(_stepsDir, step.Name + ".test.log"));
                AppendSummary(failed, 0);
                Publish(new JsonObject
                {
                    ["t"] = "step_end",
                    ["step"] = failed.Name,
                    ["mode"] = step.Mode,
                    ["attempt"] = 0,
                    ["verdict"] = "FAIL",
                    ["cause"] = cause,
                    ["secs"] = 0,
                    ["rc"] = -1
                });
                Log($"    -> FAIL (condicao: {ex.Message})");
                return failed;
            }
            if (!enabled)
            {
                Log($"SKIP {step.Name} (condicao)");
                Publish(new JsonObject
                {
                    ["t"] = "step_skip",
                    ["step"] = step.Name,
                    ["mode"] = step.Mode,
                    ["cause"] = "condicao"
                });
                return new CircuitStepResult(
                    step.Name, CircuitVerdict.Skip, 0, 0, "condicao", 0,
                    Path.Combine(_stepsDir, step.Name + ".test.log"), Skipped: true);
            }
        }

        await WaitIfPausedAsync(ct).ConfigureAwait(false);

        int attempt = NextAttempt(step.Name);
        Publish(new JsonObject
        {
            ["t"] = "step_start",
            ["step"] = step.Name,
            ["mode"] = step.Mode,
            ["attempt"] = attempt,
            ["run"] = step.RunTag ?? ""
        });

        string logPath = Path.Combine(_stepsDir, step.Name + ".test.log");
        var policy = step.Restart ?? new CircuitRestartPolicy();
        int maxAttempts = Math.Max(1, policy.MaxAttempts);
        int timeout = step.TimeoutSeconds > 0 ? step.TimeoutSeconds : _o.DefaultTimeoutSeconds;
        CircuitStepResult result = default!;
        long totalSeconds = 0;

        using var probe = StepProbe.Start(
            Path.Combine(_o.RunDirectory, "probes"),
            step.Name, step.Mode, step.ProbeUrl, step.ProbeApiKey, step.ProbeLogs);

        for (int i = 0; i < maxAttempts; i++)
        {
            if (i > 0)
            {
                Publish(new JsonObject
                {
                    ["t"] = "step_restart",
                    ["step"] = step.Name,
                    ["previous_attempt"] = attempt,
                    ["next_attempt"] = attempt + 1,
                    ["delay_sec"] = policy.DelaySeconds
                });
                if (policy.DelaySeconds > 0)
                    await Task.Delay(TimeSpan.FromSeconds(policy.DelaySeconds), ct).ConfigureAwait(false);
                await WaitIfPausedAsync(ct).ConfigureAwait(false);
                attempt = NextAttempt(step.Name);
            }

            int followStart = LineCount(step.FollowLogPath);
            var sw = Stopwatch.StartNew();
            var run = await Execution.RunAsync(step.Command, timeout, ct).ConfigureAwait(false);
            sw.Stop();
            totalSeconds += (long)sw.Elapsed.TotalSeconds;

            try { File.WriteAllText(logPath, run.Output); }
            catch (Exception ex) { Log($"ERRO ao gravar {logPath}: {ex.Message}"); }

            CopyFollowLog(step.FollowLogPath, followStart, step.FollowLogOutputPath);

            var facts = new CircuitStepRun(
                step.Name, attempt, run.Code, (long)sw.Elapsed.TotalSeconds,
                logPath, run.Output, step.Mode, step.Command.Args);

            CircuitStepOutcome outcome;
            try
            {
                outcome = step.Verdict?.Invoke(facts)
                    ?? (run.Code == 0 ? CircuitStepOutcome.Pass : CircuitStepOutcome.Fail("exit=" + run.Code));
            }
            catch (Exception ex)
            {
                outcome = CircuitStepOutcome.Fail("parser: " + ex.Message);
            }

            result = new CircuitStepResult(
                step.Name, outcome.Verdict, (long)sw.Elapsed.TotalSeconds,
                run.Code, outcome.Cause, attempt, logPath);

            bool retry = i + 1 < maxAttempts
                && !ct.IsCancellationRequested
                && ShouldRetry(policy, facts, outcome);
            if (!retry) break;
        }

        // STALE invalida o resultado: nao marca done para o step poder rodar de novo.
        // FAIL/BLOCKED tambem NAO viram done: retomar de onde parou significa
        // refazer o step que falhou, nunca pular ele no proximo run.
        if (result.Verdict is CircuitVerdict.Pass or CircuitVerdict.Skip)
        {
            try
            {
                File.WriteAllText(donePath,
                    $"{result.ExitCode} {VerdictText(result.Verdict)} {totalSeconds}");
            }
            catch (Exception ex) { Log($"ERRO ao gravar {donePath}: {ex.Message}"); }
        }
        else
        {
            TryDelete(donePath);
        }

        AppendSummary(result, totalSeconds);
        Publish(new JsonObject
        {
            ["t"] = "step_end",
            ["step"] = result.Name,
            ["mode"] = step.Mode,
            ["attempt"] = result.Attempt,
            ["verdict"] = VerdictText(result.Verdict),
            ["cause"] = result.Cause,
            ["secs"] = totalSeconds,
            ["rc"] = result.ExitCode,
            ["log"] = $"steps/{step.Name}.test.log"
        });
        Log($"    -> {VerdictText(result.Verdict)} ({totalSeconds}s, rc={result.ExitCode}"
            + (string.IsNullOrEmpty(result.Cause) ? ")" : $", causa={result.Cause})"));
        return result;
    }

    public Task<IReadOnlyList<CircuitStepResult>> RunStepsAsync(
        IEnumerable<CircuitStepSpec> steps, CancellationToken ct = default)
        => RunPlanAsync(steps, ct);

    /// <summary>
    /// Executa um plano com dependencias (DAG) e paralelismo limitado.
    /// Um step so entra quando todos os nomes de DependsOn ja terminaram;
    /// NextSteps do template pode acrescentar passos depois de cada resultado
    /// (branch). Quando MaxParallelSteps=1 o comportamento e sequencial.
    /// Dependencia ciclica/ausente falha explicitamente em vez de travar.
    /// </summary>
    private async Task<IReadOnlyList<CircuitStepResult>> RunPlanAsync(
        IEnumerable<CircuitStepSpec> steps, CancellationToken ct)
    {
        var pending = new List<CircuitStepSpec>();
        var byName = new Dictionary<string, CircuitStepSpec>(StringComparer.Ordinal);
        var results = new Dictionary<string, CircuitStepResult>(StringComparer.Ordinal);
        var ordered = new List<CircuitStepResult>();

        void Add(IEnumerable<CircuitStepSpec> items)
        {
            foreach (var step in items)
            {
                if (byName.ContainsKey(step.Name))
                    throw new InvalidOperationException(
                        $"step duplicado no plano: {step.Name}");
                byName[step.Name] = step;
                pending.Add(step);
            }
        }

        Add(steps);
        int maxParallel = Math.Clamp(_o.MaxParallelSteps, 1, 64);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var ready = pending
                .Where(s => (s.DependsOn ?? Array.Empty<string>())
                    .All(d => results.ContainsKey(d)))
                .Take(maxParallel)
                .ToList();

            if (ready.Count == 0)
            {
                string waiting = string.Join(", ", pending.Select(s =>
                    $"{s.Name} <- [{(s.DependsOn is null ? "" : string.Join(",", s.DependsOn))}]"));
                throw new InvalidOperationException(
                    "dependencia ciclica ou ausente no plano: " + waiting);
            }

            foreach (var step in ready) pending.Remove(step);
            var batch = await Task.WhenAll(ready.Select(s => RunStepAsync(s, ct)))
                .ConfigureAwait(false);

            foreach (var result in batch)
            {
                results[result.Name] = result;
                ordered.Add(result);
                Add(_template.NextSteps(this, result));
            }

            // Politica do laboratorio: erro interrompe a bateria. Os steps que
            // nao rodaram entram como SKIP com causa (nao somem do summary) e o
            // proximo run retoma daqui: PASS/SKIP ficam .done, o que falhou roda.
            if (_o.StopOnFirstFailure)
            {
                var breaker = batch.FirstOrDefault(r =>
                    r.Verdict is CircuitVerdict.Fail or CircuitVerdict.Stale or CircuitVerdict.Blocked);
                if (breaker is not null)
                {
                    var notRun = pending.ToArray();
                    pending.Clear();
                    foreach (var step in notRun)
                    {
                        var stopped = new CircuitStepResult(
                            step.Name, CircuitVerdict.Skip, 0, -1,
                            $"nao executado: {breaker.Name} falhou", 0,
                            Path.Combine(_stepsDir, step.Name + ".test.log"), Skipped: true);
                        results[step.Name] = stopped;
                        ordered.Add(stopped);
                        AppendSummary(stopped, 0);
                        Publish(new JsonObject
                        {
                            ["t"] = "step_end",
                            ["step"] = step.Name,
                            ["verdict"] = "SKIP",
                            ["cause"] = stopped.Cause
                        });
                    }

                    Log($"PARADA: {breaker.Name} falhou"
                        + (string.IsNullOrEmpty(breaker.Cause) ? "" : $" (causa={breaker.Cause})")
                        + $"; {notRun.Length} step(s) nao executado(s)");
                    break;
                }
            }
        }

        return ordered;
    }

    private static bool ShouldRetry(
        CircuitRestartPolicy policy, CircuitStepRun run, CircuitStepOutcome outcome)
    {
        if (policy.ShouldRetry is not null) return policy.ShouldRetry(run, outcome);
        return outcome.Verdict == CircuitVerdict.Fail
            && run.ExitCode is 124 or 137;
    }

    private int NextAttempt(string name)
    {
        string path = Path.Combine(_stateDir, name + ".attempt");
        int attempt = 0;
        lock (_sync)
        {
            try
            {
                if (File.Exists(path))
                    attempt = int.Parse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture);
            }
            catch
            {
                // Arquivo corrompido: recomeca em 1.
            }
            attempt++;
            try { File.WriteAllText(path, attempt.ToString(CultureInfo.InvariantCulture)); }
            catch (Exception ex) { Log($"ERRO ao gravar {path}: {ex.Message}"); }
        }
        return attempt;
    }

    /// <summary>
    /// .done so autoriza pular o step quando o veredito gravado foi PASS/SKIP.
    /// Estado legado (ou FAIL/STALE) e apagado para o step reexecutar: resume
    /// depois de correcao nunca pode pular justamente o que falhou.
    /// </summary>
    private bool DoneIsSkip(string donePath)
    {
        try
        {
            string[] parts = File.ReadAllText(donePath)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return false;
            string verdict = parts[1].ToUpperInvariant();
            if (verdict is "PASS" or "SKIP") return true;
            Log($"STATE {Path.GetFileNameWithoutExtension(donePath)}={verdict} "
                + "invalido; step sera reexecutado");
            TryDelete(donePath);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void AppendSummary(CircuitStepResult result, long seconds)
    {
        string line = string.Join('\t',
            result.Name,
            VerdictText(result.Verdict),
            seconds.ToString(CultureInfo.InvariantCulture),
            result.ExitCode.ToString(CultureInfo.InvariantCulture),
            result.Cause);
        try
        {
            lock (_sync)
                File.AppendAllText(_summaryPath, line + Environment.NewLine);
        }
        catch (Exception ex) { Log($"ERRO ao gravar {_summaryPath}: {ex.Message}"); }
    }

    private void CopyFollowLog(string? source, int startLine, string? target)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return;
        try
        {
            if (!File.Exists(source)) return;
            int end = LineCount(source);
            if (end <= startLine) return;
            var slice = File.ReadLines(source).Skip(startLine).Take(end - startLine);
            File.WriteAllText(target, string.Join(Environment.NewLine, slice) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log($"ERRO ao copiar log {source}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Recompilacao
    // ------------------------------------------------------------------
    /// <summary>
    /// Build incremental: roda se um output falta/esta velho, se Force=true ou
    /// se algum input (cs/csproj/props/targets/slnx/json/razor/cshtml/resx) e
    /// mais novo que o output mais antigo. Falha de build = Ok=false.
    /// </summary>
    public async Task<CircuitBuildResult> RecompileAsync(
        CircuitBuildSpec spec, CancellationToken ct = default)
    {
        string logPath = Path.Combine(_stepsDir, spec.Name + ".build.log");
        bool needed = spec.Force || NeedsBuild(spec);
        if (!needed)
        {
            Log($"BUILD skip {spec.Name} (artefatos atualizados)");
            Publish(new JsonObject
            {
                ["t"] = "build_skip",
                ["build"] = spec.Name,
                ["force"] = spec.Force
            });
            return new CircuitBuildResult(true, false, 0, 0, logPath, "", spec.Name);
        }

        await WaitIfPausedAsync(ct).ConfigureAwait(false);
        Publish(new JsonObject
        {
            ["t"] = "build_start",
            ["build"] = spec.Name,
            ["force"] = spec.Force
        });
        Log($"=== BUILD {spec.Name} :: {spec.Command.FileName} {string.Join(' ', spec.Command.Args)}");
        var sw = Stopwatch.StartNew();
        var run = await Execution.RunAsync(spec.Command, spec.TimeoutSeconds, ct)
            .ConfigureAwait(false);
        sw.Stop();
        try { File.WriteAllText(logPath, run.Output); }
        catch (Exception ex) { Log($"ERRO ao gravar {logPath}: {ex.Message}"); }

        bool ok = run.Code == 0;
        Publish(new JsonObject
        {
            ["t"] = "build_end",
            ["build"] = spec.Name,
            ["ok"] = ok,
            ["secs"] = (long)sw.Elapsed.TotalSeconds,
            ["rc"] = run.Code
        });
        Log($"BUILD {spec.Name} -> {(ok ? "OK" : "FAIL")} ({(long)sw.Elapsed.TotalSeconds}s, rc={run.Code})");
        if (!ok) LogTail(logPath);
        return new CircuitBuildResult(ok, true, (long)sw.Elapsed.TotalSeconds,
            run.Code, logPath, ok ? "" : "build rc=" + run.Code, spec.Name);
    }

    private bool NeedsBuild(CircuitBuildSpec spec)
    {
        if (spec.Outputs.Count == 0) return true;
        var outputs = spec.Outputs.Select(ResolvePath).ToList();
        var existing = outputs.Where(File.Exists).ToList();
        if (existing.Count != outputs.Count) return true;
        DateTime oldest = existing.Min(File.GetLastWriteTimeUtc);
        foreach (string root in spec.InputRoots)
        {
            string abs = ResolvePath(root);
            if (!Directory.Exists(abs)) continue;
            foreach (string file in EnumerateSourceFiles(abs))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > oldest) return true;
                }
                catch
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static readonly HashSet<string> BuildInputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".props", ".targets", ".slnx", ".json",
        ".razor", ".cshtml", ".resx", ".config"
    };

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(dir);
                dirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }
            foreach (string file in files)
            {
                if (BuildInputExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
            }
            foreach (string sub in dirs)
            {
                string name = Path.GetFileName(sub);
                if (name is "bin" or "obj" or ".git" or ".vs" or "node_modules") continue;
                stack.Push(sub);
            }
        }
    }

    // ------------------------------------------------------------------
    // Redeploy
    // ------------------------------------------------------------------
    public async Task<CircuitDeploymentResult> RedeployAsync(
        CircuitDeploymentSpec spec, CancellationToken ct = default)
    {
        if (spec.Stop is null && spec.Start is null)
        {
            Log($"DEPLOY skip {spec.Name} (nao configurado)");
            Publish(new JsonObject { ["t"] = "deploy_skip", ["deploy"] = spec.Name });
            return new CircuitDeploymentResult(true, true, 0, 0, "nao configurado", spec.Name);
        }

        await WaitIfPausedAsync(ct).ConfigureAwait(false);
        Publish(new JsonObject { ["t"] = "deploy_start", ["deploy"] = spec.Name });
        Log($"=== DEPLOY {spec.Name} ===");
        var sw = Stopwatch.StartNew();
        var sb = new StringBuilder();
        int rc = 0;
        string cause = "";

        if (spec.Stop is not null)
        {
            Log($"DEPLOY stop :: {spec.Stop.FileName} {string.Join(' ', spec.Stop.Args)}");
            var stop = await Execution.RunAsync(spec.Stop, spec.TimeoutSeconds, ct)
                .ConfigureAwait(false);
            sb.AppendLine("STOP rc=" + stop.Code);
            sb.AppendLine(stop.Output);
            // Stop de servico ja parado e normal; nao derruba o redeploy.
        }

        if (spec.Start is not null)
        {
            Log($"DEPLOY start :: {spec.Start.FileName} {string.Join(' ', spec.Start.Args)}");
            var start = await Execution.RunAsync(spec.Start, spec.TimeoutSeconds, ct)
                .ConfigureAwait(false);
            rc = start.Code;
            sb.AppendLine("START rc=" + start.Code);
            sb.AppendLine(start.Output);
            if (start.Code != 0) cause = "start rc=" + start.Code;
        }

        if (rc == 0 && spec.Healthy is not null)
        {
            var health = Stopwatch.StartNew();
            bool healthy = false;
            while (health.Elapsed < TimeSpan.FromSeconds(Math.Max(1, spec.TimeoutSeconds)))
            {
                ct.ThrowIfCancellationRequested();
                try { healthy = await spec.Healthy(ct).ConfigureAwait(false); }
                catch (Exception ex) { sb.AppendLine("HEALTH " + ex.Message); }
                if (healthy) break;
                int wait = Math.Max(50, spec.IntervalMs);
                await Task.Delay(Math.Min(wait, 1000), ct).ConfigureAwait(false);
            }
            sb.AppendLine("HEALTH " + (healthy ? "ok" : "timeout"));
            if (!healthy)
            {
                rc = 124;
                cause = "health timeout";
            }
        }

        sw.Stop();
        string logPath = Path.Combine(_stepsDir, spec.Name + ".deploy.log");
        try { File.WriteAllText(logPath, sb.ToString()); }
        catch (Exception ex) { Log($"ERRO ao gravar {logPath}: {ex.Message}"); }

        bool ok = rc == 0;
        Publish(new JsonObject
        {
            ["t"] = "deploy_end",
            ["deploy"] = spec.Name,
            ["ok"] = ok,
            ["secs"] = (long)sw.Elapsed.TotalSeconds,
            ["rc"] = rc,
            ["cause"] = cause
        });
        Log($"DEPLOY {spec.Name} -> {(ok ? "OK" : "FAIL")} ({(long)sw.Elapsed.TotalSeconds}s, rc={rc}"
            + (string.IsNullOrEmpty(cause) ? ")" : $", causa={cause})"));
        return new CircuitDeploymentResult(ok, false, (long)sw.Elapsed.TotalSeconds, rc, cause, spec.Name);
    }

    // ------------------------------------------------------------------
    // Gate
    // ------------------------------------------------------------------
    public CircuitGateResult Gate()
    {
        // Resume reexecuta o que falhou no mesmo run dir: o gate vale o ULTIMO
        // veredito de cada step (summary.tsv segue append-only como auditoria).
        var rows = ReadSummary()
            .GroupBy(r => r.Name, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();
        int n = rows.Count;
        int p = rows.Count(r => r.Verdict == "PASS");
        int f = rows.Count(r => r.Verdict == "FAIL");
        int s = rows.Count(r => r.Verdict == "SKIP");
        int st = rows.Count(r => r.Verdict == "STALE");
        long t = rows.Sum(r => r.Seconds);

        Log("");
        Log($"=== resumo {_template.Label} ===");
        Log($"{n} steps: PASS={p} FAIL={f} SKIP={s} STALE={st} ({t}s)");
        Log("");
        Log("=== falhas ===");
        foreach (var r in rows.Where(r => r.Verdict is "FAIL" or "STALE"))
            Log($"  {r.Name} ({r.Verdict}, rc={r.Rc}, causa={(string.IsNullOrEmpty(r.Cause) ? "-" : r.Cause)})");

        var counts = new CircuitGateResult(n, p, f, s, st, t, false);
        var policy = _template.GatePolicy(this);
        bool green = policy.IsGreen(counts);
        var gate = counts with { Green = green };

        Publish(new JsonObject
        {
            ["t"] = "gate",
            ["template"] = _template.Name,
            ["label"] = _template.Label,
            ["steps"] = n,
            ["pass"] = p,
            ["fail"] = f,
            ["skip"] = s,
            ["stale"] = st,
            ["secs"] = t,
            ["green"] = green,
            ["policy"] = policy.Name,
            ["require_green"] = _o.RequireGreen
        });
        if (_o.RequireGreen && !green)
            Log($"GATE VERMELHO: FAIL={f} STALE={st} (PASS={p}) [politica: {policy.Name}]");
        return gate;
    }

    private List<(string Name, string Verdict, long Seconds, int Rc, string Cause)> ReadSummary()
    {
        var list = new List<(string, string, long, int, string)>();
        if (!File.Exists(_summaryPath)) return list;
        try
        {
            foreach (string line in File.ReadLines(_summaryPath).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] f = line.Split('\t');
                if (f.Length < 4) continue;
                long secs = long.TryParse(f[2], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long v) ? v : 0;
                int rc = int.TryParse(f[3], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int r) ? r : 0;
                string cause = f.Length > 4 ? f[4] : "";
                list.Add((f[0], f[1], secs, rc, cause));
            }
        }
        catch (Exception ex)
        {
            Log($"ERRO ao ler {_summaryPath}: {ex.Message}");
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------
    private string ResolvePath(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(_o.Root, path);

    private void LogTail(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadLines(path).TakeLast(8))
                Log("    " + line);
        }
        catch
        {
            // Tail e diagnostico.
        }
    }

    private static int LineCount(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
        int n = 0;
        try
        {
            using var reader = new StreamReader(path);
            while (reader.ReadLine() is not null) n++;
        }
        catch
        {
            return 0;
        }
        return n;
    }

    private static string VerdictText(CircuitVerdict v) => v switch
    {
        CircuitVerdict.Pass => "PASS",
        CircuitVerdict.Fail => "FAIL",
        CircuitVerdict.Skip => "SKIP",
        CircuitVerdict.Stale => "STALE",
        CircuitVerdict.Blocked => "BLOCKED",
        _ => "UNDETERMINED"
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lock?.Dispose();
        _lock = null;
        _summaryWriter?.Dispose();
        _summaryWriter = null;
    }
}

/// <summary>
/// Lock unico de runner. FileShare.None e portavel (Windows/Linux) e liberado
/// pelo SO quando o processo morre; nao existe "lock stale" para apagar.
/// </summary>
internal sealed class CircuitLock : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    public string LockFile { get; }

    private CircuitLock(string path, FileStream stream)
    {
        LockFile = path;
        _stream = stream;
    }

    public static CircuitLock? TryAcquire(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var stream = new FileStream(path, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            try
            {
                stream.SetLength(0);
                byte[] owner = Encoding.UTF8.GetBytes(
                    $"{Environment.ProcessId}\t{Environment.MachineName}\t{DateTimeOffset.Now:O}"
                    + Environment.NewLine);
                stream.Write(owner);
                stream.Flush();
            }
            catch
            {
                // O lock vale mesmo sem gravar o PID.
            }
            return new CircuitLock(path, stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Dono do lock, se legivel; "lock em uso" quando o SO bloqueia.</summary>
    public static string DescribeOwner(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string text = sr.ReadToEnd().Trim();
            return string.IsNullOrEmpty(text) ? "livre" : text;
        }
        catch
        {
            return "lock em uso";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
