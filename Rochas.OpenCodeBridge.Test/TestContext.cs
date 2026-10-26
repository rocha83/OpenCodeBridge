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
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(120) };

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

    /// <summary>Marca o teste como pulado (ex.: cobre caminho legado desligado).</summary>
    public sealed class SkipException : Exception
    {
        public SkipException(string reason) : base(reason) { }
    }

    public static void Skip(string reason) => throw new SkipException(reason);

    public static void Run(string name, Func<Task> fn)
    {
        if (Only.Length > 0 && !name.Contains(Only, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"SKIP {name} (filtro --only)");
            return;
        }
        var t0 = DateTime.UtcNow;
        try
        {
            fn().GetAwaiter().GetResult();
            Passed++;
            Console.WriteLine($"PASS {name} ({(DateTime.UtcNow - t0).TotalSeconds:F1}s)");
        }
        catch (SkipException ex)
        {
            Skipped++;
            Console.WriteLine($"SKIP {name} ({ex.Message})");
        }
        catch (Exception ex)
        {
            Failed++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
            if (Environment.GetEnvironmentVariable("E2E_STACK") == "1") Console.WriteLine(ex.ToString());
        }
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
    public static JsonArray ToolCatalog() => new()
    {
        Fn("shell", "Executa um comando de console. Um comando por chamada; sem heredoc.", new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string" }
        }, "command"),
        Fn("write", "Cria/sobrescreve um arquivo de texto.", new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["content"] = new JsonObject { ["type"] = "string" }
        }, "path", "content"),
        Fn("read", "Le um arquivo de texto.", new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" }
        }, "path"),
        Fn("edit", "Substitui um trecho exato de um arquivo.", new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string" },
            ["oldString"] = new JsonObject { ["type"] = "string" },
            ["newString"] = new JsonObject { ["type"] = "string" }
        }, "path", "oldString", "newString"),
        Fn("glob", "Lista arquivos por padrao. Nao aceita alternancia com |.", new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string" }
        }, "pattern"),
        Fn("grep", "Busca expressao regular no conteudo dos arquivos.", new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string" }
        }, "pattern"),
        Fn("git", "Executa um comando git no repositorio. Leitura para todos; init/add/commit/config so para build/exec.", new JsonObject
        {
            ["command"] = new JsonObject { ["type"] = "string" },
            ["path"] = new JsonObject { ["type"] = "string" }
        }, "command")
    };

    static JsonObject Fn(string name, string description, JsonObject props, params string[] required)
    {
        var req = new JsonArray();
        foreach (var r in required) req.Add(r);
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = req
                }
            }
        };
    }

    /// <summary>
    /// Executa um enunciado agentico no modo corrente e devolve o texto final +
    /// histórico das tools executadas.
    /// bridge   = caminho bridge (main/tails na bridge): loop HTTP /v1/responses + /api/tools/execute;
    /// opencode = opencode run --standalone (o cliente executa as tools).
    /// </summary>
    /// <param name="prompt">Enunciado.</param>
    /// <param name="steps">Máximo de rodadas de tool no modo puro.</param>
    /// <returns>Texto final do assistente e as tools executadas.</returns>
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

    public static async Task<(string Text, List<string> Calls)> ToolAgent(string prompt, int steps, string? cwd)
    {
        string enunciadoStep = Environment.GetEnvironmentVariable("WAR_STEP")
            ?? "E-tools-full/agent/" + Mode;
        await PublishEnunciado(enunciadoStep, prompt);
        if (Mode == "opencode") return OpencodeRunDetailed(prompt, cwd);
        int max = steps > 0 ? steps : ToolAgentSteps;
        var input = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt });
        var calls = new List<string>();
        var journeyCalls = new JsonArray();
        var journeySw = Stopwatch.StartNew();
        string text = "";
        bool nudged = false;
        for (int step = 0; step < max; step++)
        {
            // /v1/responses espera a forma Responses ({type,name,description,parameters});
            // ToolCatalog() e chat-style ({type,function:{...}}). Sem esta conversao a
            // bridge nao reconhece a call de volta e o turno volta vazio (2 tok).
            var respTools = new JsonArray();
            foreach (var t in ToolCatalog())
            {
                if (t?["function"] is JsonObject fnObj)
                    respTools.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["name"] = fnObj["name"]?.DeepClone(),
                        ["description"] = fnObj["description"]?.DeepClone(),
                        ["parameters"] = fnObj["parameters"]?.DeepClone()
                    });
                else if (t is not null) respTools.Add(t.DeepClone());
            }
            var res = await PostObj(Bridge + "/v1/responses", new JsonObject
            {
                ["model"] = SuiteModel,
                ["input"] = input.DeepClone(),
                ["tools"] = respTools,
                ["stream"] = false
            });
            text = "";
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
                        string callId = item?["call_id"]?.GetValue<string>() ?? item?["id"]?.GetValue<string>() ?? "";
                        string name = item?["name"]?.GetValue<string>() ?? "";
                        string args = item?["arguments"]?.GetValue<string>() ?? "{}";
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call", ["id"] = callId, ["call_id"] = callId,
                            ["name"] = name, ["arguments"] = args
                        });
                        JsonObject exec;
                        var callSw = Stopwatch.StartNew();
                        try
                        {
                            var execBody = new JsonObject
                            {
                                ["name"] = name,
                                ["arguments"] = JsonNode.Parse(args) ?? new JsonObject(),
                                // Role do agente: a tool git libera escrita só p/ build/exec.
                                ["model"] = SuiteModel
                            };
                            // Harness resolve o diretorio da tarefa; o enunciado nao carrega path.
                            if (!string.IsNullOrWhiteSpace(cwd)) execBody["workspace"] = cwd;
                            exec = await PostObj(Bridge + "/api/tools/execute", execBody);
                        }
                        catch (Exception ex) { exec = new JsonObject { ["ok"] = false, ["error"] = ex.Message }; }
                        callSw.Stop();
                        string outText = exec["ok"]?.GetValue<bool>() == true
                            ? (exec["output"]?.GetValue<string>() ?? "")
                            : "ERRO: " + (exec["error"]?.GetValue<string>() ?? "falha");
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
                            Console.WriteLine($"  [exec] {name} ok={exec["ok"]} out={(exec["output"]?.GetValue<string>() ?? exec["error"]?.GetValue<string>() ?? "")[..Math.Min(160, (exec["output"]?.GetValue<string>() ?? exec["error"]?.GetValue<string>() ?? "").Length)]}");
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output", ["call_id"] = callId, ["output"] = outText
                        });
                    }
                }
            }
            bool hasCall = input.Count > 0 && input[input.Count - 1]!["type"]?.GetValue<string>() == "function_call_output";
            if (!hasCall)
            {
                // Turno vazio (2 tok, nenhuma tool): o modelo nao executou nada.
                // Uma cutucada explicita e o loop segue; sem isso o caso morre
                // sem artefato e o veredito vira FAIL por silencio do modelo.
                if (text.Trim().Length > 0 || nudged) break;
                nudged = true;
                input.Add(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "Voce nao emitiu nenhuma tool call. Execute a tarefa agora com as tools (write/edit/read); nao responda em prosa."
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
            if (prompt.Length > 0)
                await PublishEnunciado(Environment.GetEnvironmentVariable("WAR_STEP") ?? "model", prompt);
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
        Console.WriteLine($"[test] PASS={Passed} FAIL={Failed} SKIP={Skipped}");
        return Failed == 0 ? 0 : 1;
    }
}
