// Contexto compartilhado da suite (estado + runner + helpers HTTP + fixture soma).

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Estado global, runner e helpers HTTP da suite (BCL only).</summary>
internal static class TestContext
{
    public static string Bridge = "http://127.0.0.1:4124";
    // Upstream esperado no /api/status da bridge alvo. Default Q4 (:4100); a raia Q3
    // exporta WAR_UPSTREAM=http://127.0.0.1:4110 (run-levels: LEVEL_UPSTREAM).
    public static string Llama = Environment.GetEnvironmentVariable("WAR_UPSTREAM") is { Length: > 0 } warUp
        ? warUp : "http://127.0.0.1:4100";
    public static bool SkipE2E;
    public static bool SkipPreflight;
    public static string Only = "";
    // Upstream sem mmproj (ex.: Q3 :4110 nao carrega visao): pula os testes de imagem.
    public static bool NoVision = Environment.GetEnvironmentVariable("WAR_NO_VISION") is "1" or "true" or "True";
    public static string BeltsFilter = "";

    public static string? ApiKey;

    /// <summary>
    /// Teste declara que NAO serviu request de modelo (caminho de erro ou so' de
    /// leitura): o portao de fluxo P1 nao tem o que conferir e nao julga. Zerado
    /// pelo <see cref="Run"/> antes de cada teste, entao nao vaza para o proximo.
    /// Caso medido (2026-10-30): `E-error-mitigation` manda corpo sem `input` e
    /// espera 4xx — a bridge conta a tentativa mas nao grava `system-sent`, e o
    /// portao acusava "registro anterior ao teste".
    /// </summary>
    public static bool NoModelFlow;

    // Stream history: o driver injeta WAR_JOURNEY_DIR por step. Sem a variavel
    // (unit/manual) nada e gravado, para nao poluir o disco nem o repo.
    static string? JourneyDir => Environment.GetEnvironmentVariable("WAR_JOURNEY_DIR") is { Length: > 0 } dir
        ? dir : null;
    static string JourneyStep
    {
        get
        {
            string raw = Environment.GetEnvironmentVariable("WAR_STEP") ?? "manual-" + Mode;
            foreach (char c in Path.GetInvalidFileNameChars()) raw = raw.Replace(c, '_');
            return raw.Replace('/', '_').Replace('\\', '_');
        }
    }

    public static string? Jwt;
    // 20 min: os E2E enterprise (crud-*/garnet/worker) geram muito e, sob carga da suite
    // inteira, passavam de 600s (visto 16/10); isolados ficam em ~110-215s.
    public static HttpClient Http = NewHttp();
    static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromMinutes(120) };

    // Modelo servido pela bridge alvo (gemma|coder): expectativas por perfil.
    public static string ServedModel = "";
    public static bool ServedIsCoder => ServedModel.Contains("coder", StringComparison.OrdinalIgnoreCase);
    // Role alvo da suite (default build): a suite chama o modelo com o sufixo
    // de role para exercitar o perfil real (temp/thinking) do Build.
    public static string Role = "build";
    // --model explicito vence; sem ele, aplica o sufixo de role ao servido.
    public static string Model = "";
    // --standalone: desvia os E2E para o opencode real (mesmo enunciado),
    // em vez de HTTP direto na bridge. --standalone-agent escolhe o agent.
    public static bool Standalone;
    public static string StandaloneAgent = "build";
    public static int StandaloneTimeoutSec = 300;
    // --standalone-model explicito (provider/model); senao deriva do agent:
    // exec -> llama/ (CPU E2B), demais -> openai/ (GPU 12B).
    public static string StandaloneModel = "";
    // --standalone-provider: forca o provider do opencode (openai=Q4 :4124,
    // llama=Q3 :4125, cpu=:4126). Vazio = heuristica antiga (exec -> llama).
    public static string StandaloneProvider = "";
    public static string SuiteModel
    {
        get
        {
            if (Model.Length > 0) return Model;
            string m = ServedModel.Length > 0 ? ServedModel : "gemma-4-12b";
            bool hasRole = m.EndsWith("-plan") || m.EndsWith("-build") || m.EndsWith("-orch")
                || m.EndsWith("-moe") || m.EndsWith("-scripter") || m.EndsWith("-exec");
            return !hasRole && Role.Length > 0 ? m + "-" + Role : m;
        }
    }
    // Modo thinking da bridge (events|off): asserts de reasoning só em events.
    public static string ServedThinking = "events";
    /// <summary>Bridge com --legacy-harness 1: unico modo em que ha coercao de tools.</summary>
    public static bool ServedLegacy;

    /// <summary>Coercao de tool call so existe no modo legacy; fora dele e SKIP.</summary>
    public static void RequireCoercion()
    {
        if (!ServedLegacy) Skip("bridge sem --legacy-harness 1 (coercao desligada por design)");
    }
    public static bool WantsReasoning => ServedThinking == "events" && !ServedIsCoder;
    static int Passed;
    static int Failed;
    static int Skipped;
    /// <summary>Fail-fast da rodada: no primeiro FAIL, o resto nao executa (nao gasta GPU).</summary>
    public static bool StopOnFail = true;
    /// <summary>Verdadeiro depois do primeiro FAIL quando <see cref="StopOnFail"/> esta ligado.</summary>
    public static bool Aborted { get; private set; }
    public static string AbortedAt { get; private set; } = "";

    /// <summary>Marca o teste como pulado (ex.: cobre caminho legado desligado).</summary>
    public sealed class SkipException : Exception
    {
        public SkipException(string reason) : base(reason) { }
    }

    public static void Skip(string reason) => throw new SkipException(reason);

    /// <summary>Reinicia a bridge alvo antes de cada step E2E e espera o status.</summary>
    public static void RestartBridge()
    {
        string svc = BridgeService();
        var psi = new ProcessStartInfo("sudo", $"-n systemctl restart {svc}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var p = Process.Start(psi) ?? throw new Exception($"nao consegui executar sudo para {svc}");
        string so = p.StandardOutput.ReadToEnd();
        string se = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000) || p.ExitCode != 0)
            throw new Exception($"restart {svc} falhou (exit={p.ExitCode}): {(se + so).Trim()}");
        // O restart derruba as conexoes keep-alive dos dois lados: recria o
        // HttpClient do runner para o primeiro step nao pegar conexao morta.
        ResetHttp();
        using var probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        string? cred = Jwt ?? ApiKey;
        if (!string.IsNullOrWhiteSpace(cred))
            probeHttp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cred);
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                using var res = probeHttp.GetAsync(Bridge + "/api/status").GetAwaiter().GetResult();
                if (res.IsSuccessStatusCode)
                {
                    var st = JsonNode.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult())?.AsObject();
                    bool harness = st?["main_harness"]?.GetValue<bool>() ?? false;
                    if (Mode != "bridge" || harness) return;
                    last = new Exception("main_harness=false apos restart");
                }
            }
            catch (Exception ex) { last = ex; }
            Thread.Sleep(500);
        }
        throw new Exception($"bridge {svc} nao respondeu com status valido: {last?.Message}");
    }

    /// <summary>
    /// Fonte única do reset de diagnóstico no lado do teste: POST
    /// /api/diag/reset na bridge alvo, sem restart. Contadores e arquivos caem
    /// sempre; a sonda só com <paramref name="clearProbe"/> — na WAR a sonda é
    /// zerada uma vez por bateria, para o painel não perder a curva dos steps
    /// anteriores.
    /// </summary>
    /// <param name="clearProbe">true zera também as amostras da sonda.</param>
    /// <param name="bridge">URL alvo; null usa <see cref="Bridge"/>.</param>
    /// <param name="required">true lança em falha (run isolado); false só avisa (WAR/painel).</param>
    /// <param name="apiKey">Bearer explícito (ex.: o da WAR); null usa <see cref="Jwt"/> ou <see cref="ApiKey"/>.</param>
    /// <param name="clearPanel">true zera também o painel (enunciado, histórico e
    /// steps). Use só no início da bateria/run — no <c>step_start</c> apagaria o
    /// enunciado em execução.</param>
    public static void ResetDiagnostics(bool clearProbe = true, string? bridge = null, bool required = true,
        string? apiKey = null, bool clearPanel = false)
    {
        string target = (bridge ?? Bridge).TrimEnd('/') +
                        "/api/diag/reset?files=1" + (clearProbe ? "&probe=1" : "") +
                        (clearPanel ? "&panel=1" : "");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            string? cred = apiKey ?? Jwt ?? ApiKey;
            if (!string.IsNullOrWhiteSpace(cred))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cred);
            using var res = http.PostAsync(target, new StringContent("")).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)res.StatusCode} " +
                    res.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim());
            if (!required)
                Console.WriteLine($"OK: diagnostico zerado{(clearProbe ? " (sonda inclusa)" : "")} em {target}");
        }
        catch (Exception ex)
        {
            if (required) throw new Exception($"reset de diagnosticos falhou em {target}: {ex.Message}");
            Console.WriteLine($"AVISO: reset de diagnostico falhou em {target}: {ex.Message}");
        }
    }

    static string BridgeService()
    {
        int port = new Uri(Bridge).Port;
        return port switch
        {
            4125 => "bridge-gpu2",
            4126 => "bridge-cpu-forced",
            _ => "bridge-gpu"
        };
    }

    /// <summary>--only aceita nome unico ou lista separada por virgula (substring).</summary>
    /// <remarks>
    /// Token com `=` na frente exige o nome EXATO. Necessario desde 2026-10-30:
    /// `E-mission-algo-cs` e' prefixo de `-cs-bin/-cs-btree/-cs-kmeans/-cs-sort`, e o
    /// step do WAR que queria SO' a missao base rodava as cinco (4 PASS + 1 FAIL
    /// dentro do step errado).
    /// </remarks>
    private static bool MatchesOnly(string name)
    {
        foreach (string token in Only.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith('='))
            {
                if (name.Equals(token[1..], StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Recria o HttpClient do runner preservando a credencial.</summary>
    public static void ResetHttp()
    {
        Http = NewHttp();
        string? cred = Jwt ?? ApiKey;
        if (!string.IsNullOrWhiteSpace(cred))
            Http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", cred);
    }

    public static void Run(string name, Func<Task> fn)
    {
        if (Only.Length > 0 && !MatchesOnly(name))
        {
            Console.WriteLine($"SKIP {name} (filtro --only)");
            return;
        }
        // Fail-fast: apos o primeiro FAIL nada mais roda (sem GPU, sem rede).
        if (Aborted)
        {
            Console.WriteLine($"SKIP {name} (fail-fast: rodada abortada em {AbortedAt})");
            return;
        }
        var t0 = DateTime.UtcNow;
        // No WAR o driver ja publica step_end; aqui so o run isolado de E2E
        // alimenta o painel (evita 100+ U-* no dicionario de steps).
        bool panelStep = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAR_STEP"))
                         && name.StartsWith("E-", StringComparison.Ordinal);
        // Diagnostico: a limpeza e' UMA por bateria, no inicio (junto do rebuild +
        // redeploy/restart): Program.cs limpa no run isolado e o WarTemplate limpa
        // no inicio de cada nivel. O step NAO zera contador/sonda/painel — zerar
        // por step apagava a leitura do run no Grafana e nao deixava ver os PASS
        // acumulados (medido 2026-10-30).
        // Run isolado tambem alimenta a sonda de 5s (tps/VRAM/GPU/tools). No WAR
        // o driver e' o dono da sonda; aqui ela morre junto com o step.
        StepProbe? probe = null;
        if (panelStep)
        {
            string probeRoot = Path.Combine(Path.GetTempPath(), "opencodebridge-isolated", "run-" + Environment.ProcessId);
            probe = StepProbe.Start(probeRoot, name, Mode, Bridge, ApiKey, null);
            PublishStep(name, "RUN", 0).GetAwaiter().GetResult();
        }
        try
        {
            // Portao de fluxo (PENDINGS P1): vale para QUALQUER E2E em modo bridge.
            // O delta de model_requests diz se ESTA bridge recebeu request no run —
            // sem request nao ha fluxo a conferir (fase que usa bridge secundaria).
            NoModelFlow = false;
            long flowBefore = Mode == "bridge" && name.StartsWith("E-", StringComparison.Ordinal)
                ? BridgeFlowGate.ModelRequests(Bridge)
                : 0;
            fn().GetAwaiter().GetResult();
            // Portao de fluxo (PENDINGS P1): vale para QUALQUER E2E em modo bridge,
            // com a mesma mensagem e o mesmo dump. Em opencode nao roda (o cliente
            // se auto-gerencia) e nos U-* nao ha request de modelo para conferir.
            if (BridgeFlowGate.Applies(Mode, name, NoModelFlow))
            {
                var (missing, dump) = BridgeFlowGate.Probe(Bridge,
                    new DateTimeOffset(t0).ToUnixTimeMilliseconds(), flowBefore);
                if (missing.StartsWith("INCONCLUSIVO", StringComparison.Ordinal))
                    Console.WriteLine($"  [flow] {name}: {missing} [{dump}]");
                else if (missing.Length > 0)
                    throw new Exception("fluxo bridge incompleto: " + missing + " [" + dump + "]");
                else
                    Console.WriteLine($"  [flow] {name}: {dump}");
            }
            Passed++;
            double secs = (DateTime.UtcNow - t0).TotalSeconds;
            Console.WriteLine($"PASS {name} ({secs:F1}s)");
            if (panelStep) PublishStep(name, "PASS", secs).GetAwaiter().GetResult();
        }
        catch (SkipException ex)
        {
            Skipped++;
            Console.WriteLine($"SKIP {name} ({ex.Message})");
            if (panelStep) PublishStep(name, "SKIP", (DateTime.UtcNow - t0).TotalSeconds, ex.Message).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
            if (StopOnFail)
            {
                Aborted = true;
                AbortedAt = name;
                Console.WriteLine($"[test] FAIL-FAST: rodada abortada no primeiro FAIL ({name})");
            }
            if (Environment.GetEnvironmentVariable("E2E_STACK") == "1") Console.WriteLine(ex.ToString());
            if (panelStep) PublishStep(name, "FAIL", (DateTime.UtcNow - t0).TotalSeconds, ex.Message).GetAwaiter().GetResult();
        }
        finally
        {
            probe?.Dispose();
        }
    }

    static readonly HttpClient StepHttp = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Publica o step no painel (RUN/PASS/FAIL/SKIP) também no run isolado.</summary>
    public static async Task PublishStep(string step, string verdict, double secs, string cause = "")
    {
        try
        {
            var body = new JsonObject
            {
                ["step"] = step,
                ["mode"] = Mode,
                ["verdict"] = verdict,
                ["secs"] = secs,
                ["cause"] = cause,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, Bridge + "/api/test/step");
            if (!string.IsNullOrWhiteSpace(ApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var _ = await StepHttp.SendAsync(req);
        }
        catch { }
    }

    public static void Check(bool cond, string msg)
    {
        if (!cond) throw new Exception("assert: " + msg);
    }

    /// <summary>Base do modelo servido, sem sufixo de role (ex.: gemma-4-12b).</summary>
    static string BaseModel
    {
        get
        {
            string m = ServedModel.Length > 0 ? ServedModel : "gemma-4-12b";
            foreach (var r in new[] { "-plan", "-build", "-orch", "-moe", "-scripter", "-exec" })
                if (m.EndsWith(r)) return m[..^r.Length];
            return m;
        }
    }

    /// <summary>provider/model derivado do agent standalone (exec -> llama/E2B; demais -> openai/GPU).</summary>
    static string AutoStandaloneModel(string? agent = null)
    {
        string useAgent = string.IsNullOrWhiteSpace(agent) ? StandaloneAgent : agent;
        string role = useAgent is "plan" or "build" or "orch" or "moe" or "scripter" or "exec"
            ? useAgent : "build";
        string provider = StandaloneProvider.Length > 0
            ? StandaloneProvider + "/"
            : (useAgent == "exec" ? "llama/" : "openai/");
        return provider + BaseModel + "-" + role;
    }

    /// <summary>Processo opencode isolado do agente pai (PWD/sessao herdados ancoram no repo).</summary>
    static ProcessStartInfo OpencodePsi(string dir)
    {
        var psi = new ProcessStartInfo("opencode")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PWD"] = Path.GetFullPath(dir);
        psi.Environment.Remove("OPENCODE_SESSION_ID");
        psi.Environment.Remove("OPENCODE_TERMINAL");
        psi.Environment.Remove("OPENCODE");
        // Sandbox de teste: identidade git previsivel; o aceite continua
        // exigindo init/add/commit reais do agente.
        psi.Environment["GIT_AUTHOR_NAME"] = "WAR";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "war@local";
        psi.Environment["GIT_COMMITTER_NAME"] = "WAR";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "war@local";
        return psi;
    }

    /// <summary>Roda o mesmo enunciado pelo opencode real (standalone) e devolve stdout+stderr.</summary>
    public static string OpencodeRun(string prompt, string? cwd = null, string? agent = null, string? model = null)
        => OpencodeRunDetailed(prompt, cwd, agent, model).Text;

    /// <summary>
    /// Roda o opencode real e devolve texto + calls do stream exportado. O export
    /// completo (`opencode session export`) vira stream history antes da limpeza
    /// da sessao; sem WAR_JOURNEY_DIR (unit/manual) nada e gravado.
    /// </summary>
    public static (string Text, List<string> Calls) OpencodeRunDetailed(
        string prompt, string? cwd = null, string? agent = null, string? model = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // cwd = diretorio da tarefa: e o uso real do CLI (abre no projeto). Com cwd
        // fixo em standalone o alvo nunca esta no cwd e o modelo e forcado a
        // caminho absoluto — artefato do teste, nao do harness.
        string dir = cwd is { Length: > 0 } ? cwd : Path.Combine(Path.GetTempPath(), "opencode", "standalone");
        Directory.CreateDirectory(dir);
        var psi = OpencodePsi(dir);
        string useAgent = string.IsNullOrWhiteSpace(agent) ? StandaloneAgent : agent;
        // Agent explicito pede modelo da role; sem agent explicito, --standalone-model
        // (quando o teste setou) tem prioridade, como antes.
        string useModel = !string.IsNullOrWhiteSpace(model)
            ? model
            : (!string.IsNullOrWhiteSpace(agent)
                ? AutoStandaloneModel(useAgent)
                : (StandaloneModel.Length > 0 ? StandaloneModel : AutoStandaloneModel()));
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--standalone");   // servidor privado: nao depende do background service (evita "Timed out waiting for the background service to start")
        psi.ArgumentList.Add("--auto");         // headless: tool sem prompt de permissao (mesmo contrato do bridge)
        psi.ArgumentList.Add("--agent"); psi.ArgumentList.Add(useAgent);
        psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(useModel);
        psi.ArgumentList.Add(prompt);
        Directory.CreateDirectory(psi.WorkingDirectory);

        // Raia OpenCode limpa: cada teste comeca sem sessoes do mesmo cwd (o
        // servico global nao e tocado; o `run --standalone` e sempre novo).
        OpencodeSessionsCleanup(psi.WorkingDirectory, new HashSet<string>(StringComparer.Ordinal));

        // A sessao e criada pelo `opencode run`; a unit de teste e dona dela e
        // precisa remover o que criou (senao o opencode acumula sessoes orfas).
        var before = OpencodeSessionIds(psi.WorkingDirectory);
        string outp, errp;
        using (var p = Process.Start(psi) ?? throw new Exception("sem processo: opencode"))
        {
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(StandaloneTimeoutSec * 1000))
            {
                try { p.Kill(true); } catch { }
                throw new Exception("timeout opencode standalone");
            }
            outp = so.Result;
            errp = se.Result;
        }
        var calls = new List<string>();
        var newIds = OpencodeSessionIds(psi.WorkingDirectory)
            .Except(before, StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        if (JourneyDir is { Length: > 0 } journeyDir && newIds.Length > 0)
            RecordOpencodeJourney(journeyDir, psi.WorkingDirectory, newIds, useAgent, useModel,
                prompt, outp + errp, sw.ElapsedMilliseconds, calls);
        OpencodeSessionsCleanup(psi.WorkingDirectory, before);
        // Tempo por chamada no log: cenario lento no opencode = loop do cliente.
        Console.WriteLine($"  [opencode] {sw.Elapsed.TotalSeconds:0.0}s agent={useAgent} model={useModel} calls={calls.Count}");
        return (outp + errp, calls);
    }

    /// <summary>Exporta a sessao antes da limpeza e grava raw + meta + harness + resume.</summary>
    static void RecordOpencodeJourney(
        string journeyDir, string cwd, string[] ids, string agent, string model,
        string prompt, string output, long elapsedMs, List<string> calls)
    {
        try
        {
            Directory.CreateDirectory(journeyDir);
            string id = ids[^1];
            string raw = ExportOpencodeSession(cwd, id);
            var sessions = new JsonArray();
            foreach (string session in ids) sessions.Add(session);
            var meta = new JsonObject
            {
                ["step"] = JourneyStep,
                ["mode"] = "opencode",
                ["agent"] = agent,
                ["model"] = model,
                ["cwd"] = cwd,
                ["prompt"] = Cut(prompt, 8000),
                ["sessions"] = sessions,
                ["stdout"] = Cut(output, 12000),
                ["elapsed_ms"] = elapsedMs,
                ["export_ok"] = raw.Length > 0
            };
            if (raw.Length > 0)
            {
                // Preserva o raw por sessao (retries/script loop nao se perdem) e
                // mantem o alias .raw.json que a sonda le como "ultimo stream".
                string fullPath = Path.Combine(journeyDir, JourneyStep + ".opencode." + id + ".raw.json");
                File.WriteAllText(fullPath, raw);
                string rawPath = Path.Combine(journeyDir, JourneyStep + ".opencode.raw.json");
                File.Copy(fullPath, rawPath, overwrite: true);
                calls.AddRange(CodingJourney.CallsFromOpencode(rawPath));
                meta["export"] = rawPath;
                meta["export_full"] = fullPath;
                meta["resume"] = Path.Combine(journeyDir, JourneyStep + ".resume.sh");
                SnapshotHarness(journeyDir);
                WriteResumeScript(journeyDir, cwd, id, agent, model, rawPath);
            }
            WriteJourneyFile("opencode.meta", meta);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [journey] export opencode falhou: {ex.Message}");
        }
    }

    /// <summary>`opencode session export --standalone <id>`; vazio em falha.</summary>
    static string ExportOpencodeSession(string cwd, string id)
    {
        try
        {
            var psi = OpencodePsi(cwd);
            psi.ArgumentList.Add("session"); psi.ArgumentList.Add("export");
            psi.ArgumentList.Add("--standalone"); psi.ArgumentList.Add(id);
            using var p = Process.Start(psi);
            if (p is null) return "";
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60000)) { try { p.Kill(true); } catch { } return ""; }
            _ = se.Result;
            return p.ExitCode == 0 ? so.Result : "";
        }
        catch { return ""; }
    }

    /// <summary>Grava um diario normalizado no diretorio de stream history do step.</summary>
    static void WriteJourneyFile(string suffix, JsonNode body)
    {
        if (JourneyDir is not { Length: > 0 } dir) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, JourneyStep + "." + suffix + ".json"), body.ToJsonString());
        }
        catch (Exception ex) { Console.WriteLine($"  [journey] escrita falhou: {ex.Message}"); }
    }

    /// <summary>Copia o harness primordial capturado (docs/harness/opencode) para o bundle.</summary>
    static void SnapshotHarness(string journeyDir)
    {
        string? src = FindHarnessDir();
        if (src is null) return;
        string dst = Path.Combine(journeyDir, "harness");
        try
        {
            Directory.CreateDirectory(dst);
            foreach (string file in Directory.GetFiles(src))
                File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        }
        catch (Exception ex) { Console.WriteLine($"  [journey] harness copy falhou: {ex.Message}"); }
    }

    static string? FindHarnessDir()
    {
        string? dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "docs", "harness", "opencode");
            if (Directory.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    /// <summary>Script de retomada: importa o export e continua a sessao pelo CLI.</summary>
    static void WriteResumeScript(string journeyDir, string cwd, string id, string agent, string model, string rawPath)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("#!/usr/bin/env bash");
            sb.AppendLine("set -euo pipefail");
            sb.AppendLine("cd " + Sh(cwd));
            sb.AppendLine("# stream completo: " + rawPath);
            sb.AppendLine("opencode session import --standalone --directory " + Sh(cwd) + " " + Sh(rawPath) + " || true");
            sb.AppendLine("opencode run --standalone --auto --agent " + Sh(agent)
                + " --model " + Sh(model) + " --session " + Sh(id) + " \"$@\"");
            File.WriteAllText(Path.Combine(journeyDir, JourneyStep + ".resume.sh"), sb.ToString());
        }
        catch (Exception ex) { Console.WriteLine($"  [journey] resume falhou: {ex.Message}"); }
    }

    static string Sh(string value) => "'" + value.Replace("'", "'\\''") + "'";
    static string Cut(string value, int max) => value.Length <= max ? value : value[..max] + "...";

    /// <summary>IDs de sessao opencode do projeto (dir), lidos via `session list --format json`.</summary>
    static HashSet<string> OpencodeSessionIds(string dir)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var psi = OpencodePsi(dir);
            psi.ArgumentList.Add("session"); psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("--standalone"); psi.ArgumentList.Add("--format"); psi.ArgumentList.Add("json");
            psi.ArgumentList.Add("-n"); psi.ArgumentList.Add("200");
            using var p = Process.Start(psi);
            if (p is null) return ids;
            string json = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } return ids; }
            var arr = JsonNode.Parse(json)?.AsArray();
            if (arr is null) return ids;
            foreach (var it in arr)
                if (it is JsonObject o)
                    foreach (var kv in o)
                        if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("ses", StringComparison.Ordinal))
                        { ids.Add(s); break; }
        }
        catch { }
        return ids;
    }

    /// <summary>Remove as sessoes criadas pelo teste (diferenca antes/depois).</summary>
    static void OpencodeSessionsCleanup(string dir, HashSet<string> before)
    {
        foreach (var id in OpencodeSessionIds(dir).Where(id => !before.Contains(id)).ToArray())
        {
            try
            {
                var psi = OpencodePsi(dir);
                psi.ArgumentList.Add("session"); psi.ArgumentList.Add("delete"); psi.ArgumentList.Add("--standalone"); psi.ArgumentList.Add(id);
                using var p = Process.Start(psi);
                if (p is null) continue;
                _ = p.StandardOutput.ReadToEnd(); _ = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch { } }
                Console.WriteLine($"[cleanup] sessao opencode removida: {id}");
            }
            catch { }
        }
    }

    // --dual: roda cada teste de tools duas vezes: modo "bridge" (HTTP na bridge,
    // bridge, com a bridge executando as tools) e no modo "opencode" (opencode CLI
    // executando as tools) e modo "opencode" (cliente traz o master dele).
    public static bool Dual;

    /// <summary>Pula o bloco unit (bateria E2E com log de bridge limpo por step).</summary>
    public static bool SkipUnit;

    /// <summary>Testes de navegador (frontpipe/belts) medem na Web UI em vez da via estatica/direta.</summary>
    public static bool WebDriver;

    /// <summary>URL da Web UI usada pelos testes de navegador (default :4130).</summary>
    public static string WebDriverUrl = "http://127.0.0.1:4130";

    /// <summary>Roda os testes explicitos de CPU/Q3 (--skip-cpu desliga).</summary>
    public static bool RunCpu = true;
    public static string Mode = "bridge";
    public static int ToolAgentSteps = 8;

    /// <summary>
    /// Roda o mesmo teste nos dois modos quando --dual esta ligado
    /// (sufixo -bridge / -opencode no nome do caso).
    /// </summary>
    /// <param name="name">Nome do caso.</param>
    /// <param name="fn">Corpo do teste (le TestContext.Mode).</param>
    public static void RunDual(string name, Func<Task> fn)
    {
        if (!Dual) { Run(name, fn); return; }
        string prev = Mode;
        try
        {
            foreach (string mode in new[] { "bridge", "opencode" })
            {
                Mode = mode;
                Run(name + "-" + mode, fn);
            }
        }
        finally { Mode = prev; }
    }

    /// <summary>Catálogo completo de tools de sistema (formato Responses).</summary>
    /// <returns>Array de tools.</returns>
    /// <summary>
    /// Executa um enunciado agentico no modo corrente e devolve o texto final +
    /// histórico das tools executadas.
    /// bridge   = caminho bridge (main/tails na bridge): loop HTTP /v1/responses + /api/tools/execute;
    /// opencode = opencode run --standalone (o cliente executa as tools).
    /// </summary>
    /// <param name="prompt">Enunciado.</param>
    /// <param name="steps">Máximo de rodadas de tool no modo puro.</param>
    /// <returns>Texto final do assistente e as tools executadas.</returns>
    /// <summary>
    /// Roda um enunciado agentico com N tentativas: pasta nova por tentativa e,
    /// quando o aceite falha, a mensagem REAL da falha entra no enunciado da
    /// proxima. Fonte unica desse padrao (E-tools-full e E-ent-redis usam este
    /// metodo): medir uma saida estocastica numa unica amostra mede sorte, nao
    /// capacidade. As assertivas NAO mudam entre tentativas — a tentativa extra
    /// re-amostra o modelo com a falha explicita, sem afrouxar o contrato.
    /// </summary>
    /// <param name="label">Rotulo do log (ex.: "redis").</param>
    /// <param name="dirRoot">Raiz onde as pastas das tentativas sao criadas.</param>
    /// <param name="attempts">Numero de tentativas (>=1).</param>
    /// <param name="attempt">Recebe (pasta da tentativa, feedback da falha anterior).</param>
    /// <param name="retryHint">Orientacao do cenario para a nova tentativa (opcional).</param>
    public static async Task AgentAttempts(string label, string dirRoot, int attempts,
        Func<string, string, Task> attempt, string? retryHint = null)
    {
        string feedback = "";
        Exception? last = null;
        for (int n = 1; n <= Math.Max(1, attempts); n++)
        {
            string dir = Path.Combine(dirRoot,
                "agent-" + Environment.ProcessId + "-" + Mode + "-"
                + DateTime.Now.ToString("HHmmss") + "-t" + n);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                // Conteudo criado pela bridge (root) pode nao ser removivel pelo
                // teste: o dir novo e unico por horario, entao seguir sem apagar.
                Console.WriteLine($"  [{label}] cleanup ignorado: {ex.GetType().Name}");
            }
            Directory.CreateDirectory(dir);
            try
            {
                await attempt(dir, feedback);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Console.WriteLine($"  [{label}] tentativa {n} falhou: {ex.Message.Split('\n')[0]}");
                feedback = "\n\nTENTATIVA ANTERIOR FALHOU: " + ex.Message
                    + "\nRefaca do zero nesta pasta nova, cumprindo todos os requisitos do enunciado."
                    + (retryHint is { Length: > 0 } ? "\n" + retryHint : "");
            }
        }
        throw last!;
    }

    public static Task<(string Text, List<string> Calls)> ToolAgent(string prompt, int steps = 0)
        => ToolAgent(prompt, steps, null);

    public static async Task PublishEnunciado(string step, string prompt)
    {
        try
        {
            await PostObj(Bridge + "/api/test/enunciado",
                new JsonObject { ["step"] = step, ["prompt"] = prompt });
        }
        catch (Exception ex) { Console.WriteLine($"  [enunciado] publish falhou: {ex.Message}"); }
    }

    /// <summary>
    /// Executa uma tool pela bridge (consumidor unico dos testes). O catalogo,
    /// o schema e os handlers continuam no Core; aqui so' vai o HTTP.
    /// </summary>
    /// <summary>
    /// Filtro do catálogo canônico do Core para o cenário. Não cria schema:
    /// apenas escolhe quais tools o modelo pode ver neste teste.
    /// </summary>
    public static JsonArray Catalog(params string[] names)
    {
        var arr = new JsonArray();
        foreach (var t in global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat())
        {
            string name = t?["function"]?["name"]?.GetValue<string>() ?? "";
            if (names.Length == 0 || names.Contains(name, StringComparer.Ordinal)) arr.Add(t!.DeepClone());
        }
        return arr;
    }

    public static async Task<JsonObject> ExecuteTool(string name, JsonNode? arguments, string? cwd = null, string? model = null)
    {
        var body = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments?.DeepClone() ?? new JsonObject(),
            ["model"] = string.IsNullOrWhiteSpace(model) ? SuiteModel : model
        };
        if (!string.IsNullOrWhiteSpace(cwd)) body["workspace"] = cwd;
        try { return await PostObj(Bridge + "/api/tools/execute", body); }
        catch (Exception ex) { return new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
    }

    public static async Task<(string Text, List<string> Calls)> ToolAgent(string prompt, int steps, string? cwd)
    {
        string enunciadoStep = Environment.GetEnvironmentVariable("WAR_STEP")
            ?? "E-tools-full/agent/" + Mode;
        await PublishEnunciado(enunciadoStep, prompt);
        if (Mode == "opencode") return OpencodeRunDetailed(prompt, cwd);
        int max = steps > 0 ? steps : ToolAgentSteps;
        var input = new JsonArray();
        // Contexto de ambiente igual ao do cliente opencode: sem <env> o modelo
        // nao sabe qual e a "pasta atual" e o MainHarness manda temporarios para
        // /tmp/opencode, tirando os artefatos do dir do teste. E contexto real de
        // ambiente, nao comando enxertado no enunciado.
        if (!string.IsNullOrWhiteSpace(cwd))
            input.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = "<env>\n  Working directory: " + cwd +
                              "\n  Is directory a git repo: no\n  Platform: linux\n</env>"
            });
        input.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
        var calls = new List<string>();
        var journeyCalls = new JsonArray();
        var journeySw = Stopwatch.StartNew();
        string text = "";
        int nudges = 0;
        const int MaxNudges = 2;
        for (int step = 0; step < max; step++)
        {
            // Forma Responses vem do Core; o teste nao reescreve schema nem faz
            // conversao manual de chat para Responses.
            var respTools = global::Rochas.OpenCodeBridge.Core.ToolCatalog.Responses();
            var res = await PostObj(Bridge + "/v1/responses", new JsonObject
            {
                ["model"] = SuiteModel,
                ["input"] = input.DeepClone(),
                ["tools"] = respTools,
                ["stream"] = false
            });
            text = "";
            // Progresso contado POR ITERACAO: o modelo pode devolver output vazio
            // ou so prosa apos a primeira tool call. Antes, hasCall olhava o
            // ULTIMO item do input (sempre function_call_output apos a 1a call),
            // entao a cutucada nunca disparava e o loop girava gastando steps
            // (medido em E-ent-redis: ~10 respostas vazias apos glob sem hit).
            bool emittedCall = false;
            if (Environment.GetEnvironmentVariable("TCTX_DUMP") == "1")
                Console.WriteLine($"  [dump] {res.ToJsonString()[..Math.Min(700, res.ToJsonString().Length)]}");
            if (res["output"] is JsonArray output)
            {
                foreach (var item in output)
                {
                    string type = item?["type"]?.GetValue<string>() ?? "";
                    if (Environment.GetEnvironmentVariable("TCTX_DUMP") == "1")
                        Console.WriteLine($"  [item] type={type} name={item?["name"]} args={(item?["arguments"]?.GetValue<string>() ?? "")[..Math.Min(120, item?["arguments"]?.GetValue<string>()?.Length ?? 0)]}");
                    if (type == "message")
                    {
                        // Chaves obrigatorias: sem elas o "else if" gruda no if interno
                        // (dangling else) e a tool call deixa de ser executada.
                        foreach (var c in item?["content"]?.AsArray() ?? new JsonArray())
                            if (c?["type"]?.GetValue<string>() == "output_text") text += c?["text"]?.GetValue<string>() ?? "";
                    }
                    else if (type == "function_call")
                    {
                        emittedCall = true;
                        string callId = item?["call_id"]?.GetValue<string>() ?? item?["id"]?.GetValue<string>() ?? "";
                        string name = item?["name"]?.GetValue<string>() ?? "";
                        string args = item?["arguments"]?.GetValue<string>() ?? "{}";
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call", ["id"] = callId, ["call_id"] = callId,
                            ["name"] = name, ["arguments"] = args
                        });
                        var callSw = Stopwatch.StartNew();
                        JsonNode? callArgsNode = null;
                        try { callArgsNode = JsonNode.Parse(args); } catch { }
                        JsonObject exec = await ExecuteTool(name, callArgsNode, cwd);
                        callSw.Stop();
                        string outText = global::Rochas.OpenCodeBridge.Core.Tools.BridgeToolExecutor.FormatResultForHistory(
                            new global::Rochas.OpenCodeBridge.Core.Tools.ToolResult
                            {
                                Success = exec["ok"]?.GetValue<bool>() == true,
                                Output = exec["output"]?.GetValue<string>() ?? "",
                                Error = exec["error"]?.GetValue<string>() ?? "falha"
                            });
                        bool callOk = exec["ok"]?.GetValue<bool>() == true;
                        calls.Add($"{name}({args}) -> {(callOk ? "ok" : "fail")}");
                        JsonNode? callArgs = null;
                        try { callArgs = JsonNode.Parse(args); } catch { }
                        journeyCalls.Add(new JsonObject
                        {
                            ["round"] = step,
                            ["name"] = name,
                            ["args"] = callArgs?.DeepClone() ?? new JsonObject { ["raw"] = Cut(args, 4000) },
                            ["ok"] = callOk,
                            ["output"] = Cut(outText, 4000),
                            ["ms"] = callSw.ElapsedMilliseconds
                        });
                        if (Environment.GetEnvironmentVariable("TCTX_DUMP") == "1")
                        {
                            // Falha mostra o ERRO (output pode vir vazio e mascarar a causa).
                            string seen = exec["ok"]?.GetValue<bool>() == true
                                ? exec["output"]?.GetValue<string>() ?? ""
                                : exec["error"]?.GetValue<string>() ?? "(sem erro)";
                            Console.WriteLine($"  [exec] {name} ok={exec["ok"]} out={seen[..Math.Min(400, seen.Length)]}");
                        }
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = outText
                        });
                    }
                }
            }
            // Progresso = este response EMITIU uma tool call (nao o ultimo item
            // do input, que fica true apos a 1a call mesmo com output vazio).
            bool hasCall = emittedCall;
            if (!hasCall)
            {
                // Turno sem tool call: o modelo parou no meio da tarefa ou so
                // resumiu em prosa. Cutucada explicita de continuacao (ate
                // MaxNudges) e o loop segue; sem isso o caso morre sem artefato
                // e o veredito vira FAIL por silencio do modelo.
                if (nudges >= MaxNudges) break;
                nudges++;
                input.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = nudges == 1 && text.Trim().Length == 0
                        ? "Voce nao emitiu nenhuma tool call. Execute a tarefa agora com as tools (write/edit/read); nao responda em prosa."
                        : "A tarefa ainda nao esta concluida. Continue de onde parou e finalize todos os passos do enunciado com as tools; nao responda em prosa."
                });
                continue;
            }
        }
        journeySw.Stop();
        if (JourneyDir is { Length: > 0 })
            WriteJourneyFile("bridge", new JsonObject
            {
                ["step"] = JourneyStep,
                ["mode"] = "bridge",
                ["model"] = SuiteModel,
                ["agent"] = Role,
                ["prompt"] = Cut(prompt, 8000),
                ["text"] = Cut(text, 12000),
                ["calls"] = journeyCalls,
                ["rounds"] = max,
                ["elapsed_ms"] = journeySw.ElapsedMilliseconds
            });
        return (text, calls);
    }

    public static async Task<JsonObject> GetObj(string url)
    {
        using var res = await Http.GetAsync(url);
        string body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new Exception($"GET {url} -> {(int)res.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        return JsonNode.Parse(body)?.AsObject() ?? throw new Exception("GET resposta nao-JSON");
    }

    static string ExtractPrompt(JsonNode body)
    {
        if (body is not JsonObject obj) return "";
        JsonNode? input = obj["input"] ?? obj["messages"];
        if (input is JsonValue v && v.TryGetValue<string>(out string? s)) return s;
        if (input is not JsonArray arr) return "";
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i] is not JsonObject m) continue;
            string role = m["role"]?.GetValue<string>() ?? "";
            if (role.Length > 0 && role != "user") continue;
            JsonNode? content = m["content"];
            if (content is JsonValue cv && cv.TryGetValue<string>(out string? cs)) return cs;
            if (content is JsonArray parts)
            {
                string joined = string.Join("\n", parts.OfType<JsonObject>()
                    .Select(p => p["text"]?.GetValue<string>() ?? "")
                    .Where(t => t.Length > 0));
                if (joined.Length > 0) return joined;
            }
        }
        return "";
    }

    public static async Task<JsonObject> PostObj(string url, JsonNode body)
    {
        // Todo prompt de modelo publicado vira o enunciado do painel (inclusive
        // belts/mission, que nao passam por ToolAgent).
        if (url.Contains("/v1/", StringComparison.Ordinal))
        {
            string prompt = ExtractPrompt(body);
            // JourneyStep cai em manual-<mode> quando nao ha WAR_STEP: o run
            // isolado (--only) tambem alimenta o painel de enunciado.
            if (prompt.Length > 0)
                await PublishEnunciado(JourneyStep, prompt);
        }
        var t0 = DateTime.UtcNow;
        for (int attempt = 0; ; attempt++)
        {
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
            httpReq.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            // Declara a raia do teste: bridge recebe mainHarness+tails; opencode
            // se auto-gerencia. O CLI do opencode nao usa este caminho.
            httpReq.Headers.TryAddWithoutValidation("X-Bridge-Mode", Mode);
            using var res = await Http.SendAsync(httpReq);
            string text = await res.Content.ReadAsStringAsync();
            // Corpo vazio com 200 sob rajada (keep-alive): 1 retry após 2s.
            if (res.IsSuccessStatusCode && string.IsNullOrWhiteSpace(text) && attempt == 0)
            {
                Console.WriteLine($"  [retry] corpo vazio em {url}, tentando de novo...");
                await Task.Delay(2000);
                continue;
            }
            if (!res.IsSuccessStatusCode) throw new Exception($"POST {url} -> {(int)res.StatusCode}: {text[..Math.Min(300, text.Length)]}");
            if (string.IsNullOrWhiteSpace(text)) throw new Exception($"POST {url} -> corpo vazio apos retry");
            var obj = JsonNode.Parse(text)?.AsObject() ?? throw new Exception("POST resposta nao-JSON");
        int outTok = obj["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        if (outTok > 0)
        {
            double secs = (DateTime.UtcNow - t0).TotalSeconds;
            Console.WriteLine($"  [tps] {outTok} tok / {secs:F1}s = {(secs > 0 ? outTok / secs : 0):F1} tok/s <- {url}");
        }
        return obj;
        } // for: retry unico em corpo vazio
    }

    /// <summary>
    /// Gera o scaffold do projeto pelo template `dotnet new` (mesmo conceito dos project
    /// templates do Visual Studio): cria o .csproj/.fsproj + o fonte template na pasta alvo.
    /// O harness faz isso (nao o modelo) para os testes nao dependerem de o modelo acertar o
    /// `dotnet new`; o valor do teste fica em escrever o fonte e iterar ate rodar.
    /// </summary>
    /// <summary>
    /// Workspace limpo e UNICO por execucao para um teste E2E. Nunca derruba o
    /// teste por sobra de rodada anterior: a bridge roda como root e deixa
    /// bin/obj sem permissao para o usuario da suite; se a remocao falhar, devolve
    /// um diretorio novo (sufixo stamp-pid) em vez de falhar em 0,1 s.
    /// </summary>
    /// <param name="prefix">Prefixo do diretorio (ex.: "e2e-mission").</param>
    /// <returns>Caminho existente e vazio.</returns>
    public static string FreshWorkspace(string prefix)
    {
        string baseDir = Path.Combine(Path.GetTempPath(), prefix);
        try
        {
            if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
            Directory.CreateDirectory(baseDir);
            return baseDir;
        }
        catch (Exception ex)
        {
            string alt = baseDir + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId;
            Directory.CreateDirectory(alt);
            Console.WriteLine($"  [ws] cleanup ignorado em {baseDir} ({ex.GetType().Name}); usando {alt}");
            return alt;
        }
    }

    public static void ScaffoldProject(string name, string dir, string lang = "C#", string kind = "console", int secs = 180)
    {
        Directory.CreateDirectory(dir);
        string args = $"new {kind} -n {name} -o {dir}" + (lang.Equals("C#", StringComparison.OrdinalIgnoreCase) ? "" : $" -lang {lang}");
        var psi = new ProcessStartInfo("dotnet", args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["DOTNET_ROLL_FORWARD"] = "LatestMajor";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi) ?? throw new Exception("sem processo: dotnet new");
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout dotnet new " + name); }
        string outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        if (p.ExitCode != 0) throw new Exception($"dotnet new {name} falhou ({p.ExitCode}): {outp[..Math.Min(300, outp.Length)]}");
    }

    public static JsonObject SomaToolResponses() => new()    {
        ["type"] = "function",
        ["name"] = "soma",
        ["description"] = "Soma dois inteiros",
        ["parameters"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["a"] = new JsonObject { ["type"] = "integer" },
                ["b"] = new JsonObject { ["type"] = "integer" }
            },
            ["required"] = new JsonArray("a", "b")
        }
    };

    public static int Summary()
    {
        if (Aborted) Console.WriteLine($"[test] ABORTADO (fail-fast) no primeiro FAIL: {AbortedAt} — restantes nao executados");
        Console.WriteLine($"[test] PASS={Passed} FAIL={Failed} SKIP={Skipped}");
        return Failed == 0 ? 0 : 1;
    }
}
