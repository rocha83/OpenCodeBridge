using System.Text.Json;
using Microsoft.Playwright;

namespace Rochas.OpenCodeBridge.Web.Test.Ui;

// E2E das faixas pela UI real (Playwright + Firefox, UA Win11):
// login livre -> /Chat -> cria sessao (orch + executor) -> Decompose ->
// aprova (needsTools conforme o modo) -> OrchestrateApproved -> poll /Tasks -> Synthesize.
// Bateria: azul (aquecimento), verde, roxa, marrom, preta (prompts das sessoes -C/v3 do web.db).
// Modo plan: so decompoe e valida o descritivo (sem ato executivo).
// Modo build: execucao real com tools nos executores 3B.
public static class BeltUiTests
{
    // UA fixo de Firefox no Windows 11 (requisito do cenario).
    private const string Win11FirefoxUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0";

    // Orquestrador 8B (id 1 build / id 6 plan) + executores (2 plan CPU, 4/5 build GPU, 7/8 plan GPU).
    private const int BuildOrchestratorId = 1;
    private const int PlanOrchestratorId = 6;
    private const int PlanExecutorId = 2;
    private static int PlanGpuExecutor(string key) => key == "roxa" || key == "preta" ? 8 : 7;

    private sealed record Belt(string Key, string Title, string Prompt, int ExecutorId, int TimeoutMin);

    private static readonly Belt[] Belts =
    {
        new("azul", "Faixa AZUL E2E",
            "Especifique um CRUD de reembolsos corporativos: modelo de dados com campos, regras de aprovacao por faixa de valor e os endpoints necessarios. Premissas: backend .NET C#, frontend em MVC Razor.",
            4, 30),
        new("verde", "Faixa VERDE E2E",
            "Defina as regras de validacao de CPF e CNPJ para cadastro de clientes e fornecedores: digitos verificadores, rejeicao de sequencias repetidas e numeros de teste conhecidos, aceitacao com e sem mascara, obrigatoriedade por tipo de pessoa (PF exige CPF, PJ exige CNPJ), e de 2 exemplos validos e 1 invalido de cada. Inclua calculo de distancia haversiana entre dois CEPs a partir de base local de CEP com campos lat/lng (amostra embutida no enunciado, sem rede), com 1 exemplo calculado.",
            4, 30),
        new("roxa", "Faixa ROXA E2E",
            "Defina validacao e mascaras para e-mail, telefone BR com DDD e CEP: regras, regex de cada um e 2 exemplos validos de cada.",
            5, 30),
        new("marrom", "Faixa MARROM E2E",
            "Especifique um CRUD de reembolsos corporativos: modelo de dados com campos, regras de aprovacao por faixa de valor, endpoints necessarios, e pipeline CI/CD com Docker (Dockerfile multi-stage e compose para subir api+db).",
            4, 90),
        new("preta", "Faixa PRETA E2E",
            "Arquitetura de ecossistema (divida em cerca de 14 subtarefas): Portal do Colaborador (ponto eletronico, reembolsos, organograma) integrado via barramento de eventos assincrono a outros sistemas (folha, ERP); APIs REST do portal; pipeline de Big Data com ML: regressao para previsao de gastos, classificacao de reembolsos suspeitos e rede neural (perceptron multicamadas) para deteccao de anomalias em ponto eletronico. Premissas: backend .NET C#, frontend do portal em React.js.",
            5, 120),
    };

    public static async Task<int> RunAsync(string web, string[] only, string mode)
    {
        return await RunAsync(web, only, mode, "full");
    }

    // Fases do build (respeito ao swap unico): decompose (8B) -> execute (3B) -> review (8B).
    // --phase decompose|execute|review; full = plan-descritivo ou build com 8B+3B coabitando (nao usado em swap).
    public static async Task<int> RunAsync(string web, string[] only, string mode, string phase)
    {
        int failures = 0;
        bool build = mode == "build";
        var wanted = only.Length == 0
            ? Belts
            : Belts.Where(b => only.Contains(b.Key)).ToArray();
        if (wanted.Length == 0)
        {
            Console.WriteLine("[ui] FAIL nenhuma faixa selecionada");
            return 1;
        }

        try
        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "firefox",
                Headless = true,
            });
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                UserAgent = Win11FirefoxUa,
            });
            var page = await context.NewPageAsync();

            // Login livre (qualquer senha) + chega ao /Chat (navegacao UI real).
            await page.GotoAsync(web + "/Account/Login");
            await page.FillAsync("input[name=email]", "admin@mova.com");
            await page.FillAsync("input[name=password]", "e2e");
            await page.ClickAsync("form button:has-text('Entrar')");
            await page.WaitForURLAsync(url => !url.Contains("/Account/Login"), new PageWaitForURLOptions { Timeout = 15000 });
            await page.GotoAsync(web + "/Chat");
            await page.WaitForSelectorAsync("#conv, #prompt, select#agentId", new PageWaitForSelectorOptions { Timeout = 15000 });
            Console.WriteLine("[ui] PASS login + /Chat (UA Win11, Firefox)");

            // API via HttpClient com o cookie da sessao do browser (estavel; fetch no
            // contexto da pagina sofria 500 deterministico no Dapper em POST /Chat/Sessions).
            using var api = BuildApiClient(web, await context.CookiesAsync());

            foreach (var belt in wanted)
                failures += phase switch
                {
                    "decompose" => await DecomposeBeltAsync(api, belt),
                    "plan" => await PlanBeltAsync(api, belt),
                    "reviewplan" => await ReviewPlanAsync(api, belt),
                    "execute" => await ExecuteBeltAsync(api, belt),
                    "review" => await ReviewBeltAsync(api, belt),
                    _ => await RunBeltAsync(api, web, belt, build),
                };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui] FAIL harness: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
            return 1;
        }

        return failures;
    }

    // HttpClient com os cookies do contexto Playwright (mesma sessao autenticada).
    private static HttpClient BuildApiClient(string web, IReadOnlyList<BrowserContextCookiesResult> cookies)
    {
        var jar = new System.Net.CookieContainer();
        foreach (var c in cookies)
            jar.Add(new System.Net.Cookie(c.Name, c.Value, c.Path ?? "/", c.Domain.TrimStart('.')));
        return new HttpClient(new HttpClientHandler { CookieContainer = jar })
        {
            BaseAddress = new Uri(web + "/"),
            Timeout = TimeSpan.FromMinutes(15), // Decompose/synthesize no 8B levam minutos
        };
    }

    private static string TaskFile(string key) => $"/tmp/opencode/belt_{key}.json";
    private static string BuildFile(string key) => $"/tmp/opencode/belt_{key}_build.json";
    private static string ExecFile(string key) => $"/tmp/opencode/belt_{key}_exec.json";

    // Extrai blocos ```sh|bash dos prompts para .sh auditaveis em disco + valida sintaxe (bash -n).
    private static void SaveScripts(string key, List<(string Title, string Prompt, double EtaMin)> tasks)
    {
        string dir = $"/tmp/opencode/scripts/{key}";
        Directory.CreateDirectory(dir);
        int n = 0, bad = 0;
        foreach (var (t, i) in tasks.Select((t, i) => (t, i)))
        {
            var blocks = System.Text.RegularExpressions.Regex.Matches(
                t.Prompt, "```(?:sh|bash)\\s*\\n(.*?)```",
                System.Text.RegularExpressions.RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value.Trim()).ToList();
            if (blocks.Count == 0)
            {
                // Fallback: script sem fence (a partir do shebang ate o fim do prompt).
                int at = t.Prompt.IndexOf("#!/bin/bash", StringComparison.Ordinal);
                if (at >= 0) blocks.Add(t.Prompt[at..].Trim());
            }
            foreach (string body in blocks)
            {
                string path = Path.Combine(dir, $"tarefa_{i + 1:00}.sh");
                File.WriteAllText(path, "#!/bin/bash\nset -euo pipefail\n" + body + "\n");
                n++;
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("bash", $"-n \"{path}\"")
                    {
                        RedirectStandardError = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    if (p.ExitCode != 0)
                    {
                        bad++;
                        Console.WriteLine($"[ui] {key}: tarefa {i + 1} script com erro de sintaxe: {err.Split('\n')[0]}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ui] {key}: bash -n indisponível ({ex.Message.Split('\n')[0]})");
                }
            }
        }
        Console.WriteLine($"[ui] {key}: {n} scripts .sh salvos em {dir} ({bad} com erro de sintaxe)");
    }

    // Anexo: o 8B (sempre em plan) conhece as tools para detalhar o enunciado dos workers.
    private const string ToolsAnnex =
        " Ferramentas dos executores: shell (comando, cwd travado), read (path, offset, limit), " +
        "write (path, content), edit (path, oldString, newString), grep (pattern, path), glob (pattern). " +
        "Detalhe cada tarefa com a ferramenta exata, argumentos e ACEITE em comando executável.";
    // Anexo: quem executa (p/ calibrar granularidade e complexidade).
    private const string WorkersAnnex =
        " Executores: 2 instâncias Qwen2.5-Coder-3B-AWQ na GPU (ctx 16k, thinking desligado). " +
        "Modelos pequenos: micro-enunciados curtos, 1 ação verificável, sem ambiguidade.";
    // Anexo: script bash por tarefa (executores rodam em build; fallback: tools).
    private const string ScriptAnnex =
        " Para CADA tarefa, inclua no prompt um script bash (shebang + set -euo pipefail) " +
        "usando SOMENTE: ls cat head tail echo sed grep find wc diff file pwd date git dotnet " +
        "python3 curl. PROIBIDO bash -c aninhado, mkdir -p encadeado, pipes com efeito colateral, " +
        "comandos fictícios (python3 -m ...) e placeholder. Os executores rodam o script em build.";

    // Fase decompose (8B no ar): cria sessao build, decompoe, salva tarefas em arquivo.
    private static async Task<int> DecomposeBeltAsync(HttpClient api, Belt belt)
    {
        try
        {
            int sessionId = await CreateSessionAsync(api, BuildOrchestratorId, belt.ExecutorId, belt.Title + " Build");
            var approved = await DecomposeTasksAsync(api, belt, sessionId);
            if (approved.Count == 0) return 1;
            var file = new
            {
                sessionId,
                orch = BuildOrchestratorId,
                executor = belt.ExecutorId,
                tasks = approved.Select(t => new { title = t.Title, prompt = t.Prompt, etaMin = t.EtaMin }).ToArray(),
            };
            await File.WriteAllTextAsync(TaskFile(belt.Key), JsonSerializer.Serialize(file));
            Console.WriteLine($"[ui] PASS {belt.Key}: decompose {approved.Count} tarefas (sessao {sessionId})");
            SaveScripts(belt.Key, approved);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui] FAIL {belt.Key}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }

    // Fase plan (3B GPU em modo plan, sem tools): descreve o desenvolvimento de cada tarefa.
    private static async Task<int> PlanBeltAsync(HttpClient api, Belt belt)
    {
        try
        {
            int sessionId = await CreateSessionAsync(api, PlanOrchestratorId, PlanGpuExecutor(belt.Key), belt.Title + " Plan");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(TaskFile(belt.Key)));
            var payload = doc.RootElement.GetProperty("tasks").EnumerateArray().Select(t => new
            {
                title = t.GetProperty("title").GetString(),
                prompt = t.GetProperty("prompt").GetString(),
                etaMin = t.GetProperty("etaMin").GetDouble(),
                needsTools = false,
            }).ToArray();
            await PostAsync(api, "Chat/OrchestrateApproved",
                JsonSerializer.Serialize(new { sessionId, tasks = payload, synthesize = false }), "taskCount");
            bool done = await WaitTasksDoneAsync(api, "", sessionId, payload.Length, belt.TimeoutMin);
            Console.WriteLine($"[ui] {(done ? "PASS" : "FAIL")} {belt.Key}: plan {payload.Length} descricoes (sessao {sessionId})");
            if (done)
            {
                using var tdoc = JsonDocument.Parse(await File.ReadAllTextAsync(TaskFile(belt.Key)));
                using var ms = new MemoryStream();
                using (var w = new Utf8JsonWriter(ms))
                {
                    w.WriteStartObject();
                    foreach (var p in tdoc.RootElement.EnumerateObject())
                    {
                        if (p.Name == "planSessionId") continue;
                        p.WriteTo(w);
                    }
                    w.WriteNumber("planSessionId", sessionId);
                    w.WriteEndObject();
                }
                await File.WriteAllBytesAsync(TaskFile(belt.Key), ms.ToArray());
            }
            return done ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui] FAIL {belt.Key}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }

    // Fase reviewplan (8B de volta): revisa as descricoes do plan e grava o arquivo de build
    // com enunciados corrigidos (aprovadas mantem o original).
    private static async Task<int> ReviewPlanAsync(HttpClient api, Belt belt)
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(TaskFile(belt.Key)));
            int sessionId = doc.RootElement.GetProperty("sessionId").GetInt32();
            int planSessionId = doc.RootElement.GetProperty("planSessionId").GetInt32();
            var tasks = doc.RootElement.GetProperty("tasks").EnumerateArray().ToArray();

            using var res = await api.GetAsync($"Chat/Sessions/{planSessionId}/Messages?limit=200");
            string raw = await res.Content.ReadAsStringAsync();
            res.EnsureSuccessStatusCode();
            using var msgs = JsonDocument.Parse(raw);
            var assistants = msgs.RootElement.EnumerateArray()
                .Where(m => m.TryGetProperty("role", out var r) && r.GetString() == "assistant")
                .Select(m => m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "")
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Voce e o revisor. Para cada descricao de desenvolvimento abaixo, responda APROVADA se descreve acao tecnica verificavel, REJEITADA se vaga ou sem aceite, com enunciado corrigido.");
            sb.AppendLine("Seja BREVE: reason 1 linha, fixedPrompt 3 linhas max. NENHUMA prosa fora do JSON. Responda SOMENTE JSON: [{\"index\":1,\"verdict\":\"APROVADA\",\"reason\":\"...\",\"fixedPrompt\":\"...\"}]");
            for (int i = 0; i < tasks.Length; i++)
            {
                string title = tasks[i].GetProperty("title").GetString() ?? "";
                string mark = $"[Executor {i + 1}] Concluído";
                string result = assistants.LastOrDefault(c => c.Contains(mark)) ?? "(sem descricao)";
                sb.AppendLine($"--- Tarefa {i + 1}: {title} ---");
                sb.AppendLine(result.Length > 1500 ? result[..1500] : result);
            }
            var verdicts = await AskReviewAsync(sb.ToString());

            var buildTasks = new List<object>();
            int approved = 0;
            foreach (var (t, i) in tasks.Select((t, i) => (t, i)))
            {
                var v = verdicts.Where(x => x.Index == i + 1).FirstOrDefault();
                bool found = v.Index == i + 1;
                bool ok = found && v.Verdict.Contains("APROVADA", StringComparison.OrdinalIgnoreCase);
                string prompt = (!ok && found && v.Fixed.Length > 0)
                    ? v.Fixed : (t.GetProperty("prompt").GetString() ?? "");
                if (ok) approved++;
                else Console.WriteLine($"[ui] {belt.Key}: plan tarefa {i + 1} REJEITADA: {(found ? v.Reason : "sem veredito")}");
                buildTasks.Add(new
                {
                    title = t.GetProperty("title").GetString(),
                    prompt,
                    etaMin = t.GetProperty("etaMin").GetDouble(),
                });
            }
            await File.WriteAllTextAsync(BuildFile(belt.Key), JsonSerializer.Serialize(new
            {
                sessionId,
                tasks = buildTasks.ToArray(),
            }));
            Console.WriteLine($"[ui] PASS {belt.Key}: reviewplan {approved}/{tasks.Length} aprovadas");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui] FAIL {belt.Key}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }

    // Uma chamada de revisao ao 8B via bridge direta (fora do swap: 8B no ar).
    private static async Task<List<(int Index, string Verdict, string Reason, string Fixed)>> AskReviewAsync(string content)
    {
        using var bridge = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var rres = await bridge.PostAsync("http://127.0.0.1:4124/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(new
            {
                model = "qwen3-8b-awq",
                temperature = 0.1,
                max_tokens = 4096,
                messages = new[] { new { role = "user", content } },
            }), System.Text.Encoding.UTF8, "application/json"));
        string rtext = await rres.Content.ReadAsStringAsync();
        rres.EnsureSuccessStatusCode();
        string verdict = JsonDocument.Parse(rtext).RootElement
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "[]";
        try { await File.WriteAllTextAsync("/tmp/opencode/review_raw.txt", verdict); } catch { }
        int s = verdict.IndexOf('['), e = verdict.LastIndexOf(']');
        string array = s >= 0 && e > s ? verdict.Substring(s, e - s + 1) : "[]";
        // Modelo emite escapes invalidos (ex.: \w de regex): escapa backslash solitaria.
        array = System.Text.RegularExpressions.Regex.Replace(array, @"\\(?![\""\\/bfnrtu])", @"\\");
        List<(int Index, string Verdict, string Reason, string Fixed)> out_ = new();
        try
        {
            using var ver = JsonDocument.Parse(array);
            out_ = ver.RootElement.EnumerateArray().Select(v => (
                Index: v.TryGetProperty("index", out var ix) ? ix.GetInt32() : -1,
                Verdict: v.TryGetProperty("verdict", out var vv) ? (vv.GetString() ?? "") : "",
                Reason: v.TryGetProperty("reason", out var rr) ? (rr.GetString() ?? "") : "",
                Fixed: v.TryGetProperty("fixedPrompt", out var fp) ? (fp.GetString() ?? "") : "")).ToList();
        }
        catch
        {
            // Fallback: extrai vereditos por regex, um a um (ignora objetos quebrados).
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(array,
                @"\{[^{}]*""index""\s*:\s*(\d+)[^{}]*""verdict""\s*:\s*""(APROVADA|REJEITADA)""[^{}]*\}"))
                out_.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value, "", ""));
        }
        return out_;
    }

    // Fase execute (workers no ar, 8B fora): aprova com needsTools e aguarda conclusao, sem sintetizar.
    // --exec ID: força um executor unico (1×modelo full); vazio = alternado 4/5 por faixa.
    private static int ExecOverride = 0;

    public static void SetExecOverride(int id) => ExecOverride = id;
    private static async Task<int> ExecuteBeltAsync(HttpClient api, Belt belt)
    {
        try
        {
            string file = File.Exists(BuildFile(belt.Key)) ? BuildFile(belt.Key) : TaskFile(belt.Key);
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            int sessionId = doc.RootElement.GetProperty("sessionId").GetInt32();
            if (ExecOverride > 0)
            {
                // 1×modelo: nova sessao build com o executor unico.
                sessionId = await CreateSessionAsync(api, BuildOrchestratorId, ExecOverride, belt.Title + " Build 1x");
                Console.WriteLine($"[ui] {belt.Key}: sessao build unica {sessionId} (executor {ExecOverride})");
            }
            var payload = doc.RootElement.GetProperty("tasks").EnumerateArray().Select(t => new
            {
                title = t.GetProperty("title").GetString(),
                prompt = t.GetProperty("prompt").GetString(),
                etaMin = t.GetProperty("etaMin").GetDouble(),
                needsTools = true,
            }).ToArray();
            await PostAsync(api, "Chat/OrchestrateApproved",
                JsonSerializer.Serialize(new { sessionId, tasks = payload, synthesize = false }), "taskCount");
            bool done = await WaitTasksDoneAsync(api, "", sessionId, payload.Length, belt.TimeoutMin);
            Console.WriteLine($"[ui] {(done ? "PASS" : "FAIL")} {belt.Key}: {payload.Length} tarefas executadas (sessao {sessionId})");
            if (done)
                await File.WriteAllTextAsync(ExecFile(belt.Key), JsonSerializer.Serialize(new { sessionId }));
            return done ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui] FAIL {belt.Key}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }

    // Fase review (8B de volta): revisao em lote dos resultados, re-executa rejeitadas, sintetiza.
    private static async Task<int> ReviewBeltAsync(HttpClient api, Belt belt)
    {
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(TaskFile(belt.Key)));
            int sessionId = doc.RootElement.GetProperty("sessionId").GetInt32();
            // Build rodou em sessao propria (--exec): revisa nela.
            try
            {
                using var exec = JsonDocument.Parse(await File.ReadAllTextAsync(ExecFile(belt.Key)));
                sessionId = exec.RootElement.GetProperty("sessionId").GetInt32();
            }
            catch { }
            var tasks = doc.RootElement.GetProperty("tasks").EnumerateArray().ToArray();

            using var res = await api.GetAsync($"Chat/Sessions/{sessionId}/Messages?limit=200");
            string raw = await res.Content.ReadAsStringAsync();
            res.EnsureSuccessStatusCode();
            using var msgs = JsonDocument.Parse(raw);
            var byRole = msgs.RootElement.EnumerateArray()
                .Where(m => m.TryGetProperty("role", out var r) && r.GetString() == "assistant")
                .Select(m => m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "")
                .ToList();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Voce e o revisor. Para cada tarefa abaixo, responda APROVADA ou REJEITADA + motivo curto + enunciado corrigido se rejeitada.");
            sb.AppendLine("Seja BREVE: reason 1 linha, fixedPrompt 3 linhas max. NENHUMA prosa fora do JSON. Responda SOMENTE JSON: [{\"index\":1,\"verdict\":\"APROVADA\",\"reason\":\"...\",\"fixedPrompt\":\"...\"}]");
            for (int i = 0; i < tasks.Length; i++)
            {
                string title = tasks[i].GetProperty("title").GetString() ?? "";
                string mark = $"[Executor {i + 1}] Concluído";
                string result = byRole.LastOrDefault(c => c.Contains(mark)) ?? "(sem resultado)";
                sb.AppendLine($"--- Tarefa {i + 1}: {title} ---");
                sb.AppendLine(result.Length > 1500 ? result[..1500] : result);
            }

            using var bridge = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
            using var rres = await bridge.PostAsync("http://127.0.0.1:4124/v1/chat/completions",
                new StringContent(JsonSerializer.Serialize(new
                {
                    model = "qwen3-8b-awq",
                    temperature = 0.1,
                    max_tokens = 4096,
                    messages = new[] { new { role = "user", content = sb.ToString() } },
                }), System.Text.Encoding.UTF8, "application/json"));
            string rtext = await rres.Content.ReadAsStringAsync();
            rres.EnsureSuccessStatusCode();
            string verdict = JsonDocument.Parse(rtext).RootElement
                .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "[]";
            int s = verdict.IndexOf('['), e = verdict.LastIndexOf(']');
            using var ver = JsonDocument.Parse(s >= 0 && e > s ? verdict.Substring(s, e - s + 1) : "[]");
            var rejected = new List<object>();
            foreach (var v in ver.RootElement.EnumerateArray())
            {
                int idx = v.TryGetProperty("index", out var ix) ? ix.GetInt32() - 1 : -1;
                string vd = v.TryGetProperty("verdict", out var vv) ? (vv.GetString() ?? "") : "";
                if (idx >= 0 && idx < tasks.Length && vd.Contains("REJEITADA", StringComparison.OrdinalIgnoreCase))
                {
                    string fixed_ = v.TryGetProperty("fixedPrompt", out var fp) && fp.GetString()?.Length > 0
                        ? fp.GetString()! : (tasks[idx].GetProperty("prompt").GetString() ?? "");
                    rejected.Add(new
                    {
                        title = tasks[idx].GetProperty("title").GetString(),
                        prompt = fixed_,
                        etaMin = tasks[idx].GetProperty("etaMin").GetDouble(),
                        needsTools = true,
                    });
                    string reason = v.TryGetProperty("reason", out var rr) ? (rr.GetString() ?? "") : "";
                    Console.WriteLine($"[ui] {belt.Key}: tarefa {idx + 1} REJEITADA: {reason}");
                }
            }
            Console.WriteLine($"[ui] {belt.Key}: revisao {tasks.Length - rejected.Count}/{tasks.Length} aprovadas");

            if (rejected.Count > 0)
            {
                await PostAsync(api, "Chat/OrchestrateApproved",
                    JsonSerializer.Serialize(new { sessionId, tasks = rejected.ToArray(), synthesize = false }), "taskCount");
                bool done = await WaitTasksDoneAsync(api, "", sessionId, rejected.Count, belt.TimeoutMin);
                if (!done) { Console.WriteLine($"[ui] FAIL {belt.Key}: re-execucao incompleta"); return 1; }
            }

            string synthesis = await PostAsync(api, "Chat/Synthesize",
                JsonSerializer.Serialize(new { sessionId }), "synthesis");
            bool synthOk = synthesis.Length > 100;
            Console.WriteLine($"[ui] {(synthOk ? "PASS" : "FAIL")} {belt.Key}: sintese {synthesis.Length} chars");
            return synthOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui] FAIL {belt.Key}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }

    private static async Task<int> CreateSessionAsync(HttpClient api, int orch, int executor, string title)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                return int.Parse(await PostAsync(api, "Chat/Sessions",
                    JsonSerializer.Serialize(new { agentId = orch, executorAgentId = executor, title }), "id"));
            }
            catch when (attempt < 3)
            {
                await Task.Delay(2000);
            }
        }
        throw new InvalidOperationException("criar sessao falhou 3x");
    }

    private static async Task<List<(string Title, string Prompt, double EtaMin)>> DecomposeTasksAsync(HttpClient api, Belt belt, int sessionId, string? text = null)
    {
        for (int dt = 1; dt <= 2; dt++)
        {
            string tasksJson = await PostAsync(api, "Chat/Decompose",
                JsonSerializer.Serialize(new { sessionId, text = text ?? belt.Prompt }), "tasks");
            using var tasksDoc = JsonDocument.Parse(tasksJson);
            var arr = tasksDoc.RootElement.EnumerateArray().ToArray();
            bool ok = arr.Length is >= 4 and <= 16
                && arr.All(t => t.TryGetProperty("title", out var ti) && ti.GetString()?.Length > 0
                    && t.TryGetProperty("prompt", out var pr) && pr.GetString()?.Length > 0);
            if (ok)
                return arr.Select(t => (
                    Title: t.GetProperty("title").GetString()!,
                    Prompt: t.GetProperty("prompt").GetString()!,
                    EtaMin: t.TryGetProperty("etaMin", out var e) ? e.GetDouble() : 3)).ToList();
            if (dt == 1)
                Console.WriteLine($"[ui] {belt.Key}: decompose fora da forma ({arr.Length}), retry");
        }
        Console.WriteLine($"[ui] FAIL {belt.Key}: decompose fora da forma 2x");
        return new List<(string, string, double)>();
    }

    private static async Task<int> RunBeltAsync(HttpClient api, string web, Belt belt, bool build)
    {
        int orch = build ? BuildOrchestratorId : PlanOrchestratorId;
        int executor = build ? belt.ExecutorId : PlanExecutorId;
        try
        {
            // Cria sessao via UI: orch + executor do modo (retry: Query Dapper tem race transitoria).
            int sessionId = 0;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    sessionId = int.Parse(await PostAsync(api, "Chat/Sessions",
                        JsonSerializer.Serialize(new { agentId = orch, executorAgentId = executor, title = belt.Title }),
                        "id"));
                    break;
                }
                catch when (attempt < 3)
                {
                    Console.WriteLine($"[ui] {belt.Key}: retry criar sessao ({attempt})");
                    await Task.Delay(2000);
                }
            }
            if (sessionId <= 0) throw new InvalidOperationException("criar sessao falhou 3x");
            Console.WriteLine($"[ui] {belt.Key}: sessao {sessionId} (executor {executor})");

            // Decompose: 8B amplia o entendimento e segmenta em tarefas atomicas (1 retry de forma).
            var approved = new List<(string Title, string Prompt, double EtaMin)>();
            bool shapeOk = false;
            for (int dt = 1; dt <= 2 && !shapeOk; dt++)
            {
                string tasksJson = await PostAsync(api, "Chat/Decompose",
                    JsonSerializer.Serialize(new { sessionId, text = belt.Prompt }), "tasks");
                using var tasksDoc = JsonDocument.Parse(tasksJson);
                var arr = tasksDoc.RootElement.EnumerateArray().ToArray();
                shapeOk = arr.Length is >= 4 and <= 16
                    && arr.All(t => t.TryGetProperty("title", out var ti) && ti.GetString()?.Length > 0
                        && t.TryGetProperty("prompt", out var pr) && pr.GetString()?.Length > 0);
                if (shapeOk)
                    approved = arr.Select(t => (
                        Title: t.GetProperty("title").GetString()!,
                        Prompt: t.GetProperty("prompt").GetString()!,
                        EtaMin: t.TryGetProperty("etaMin", out var e) ? e.GetDouble() : 3)).ToList();
                else if (dt == 1)
                    Console.WriteLine($"[ui] {belt.Key}: decompose fora da forma ({arr.Length}), retry");
            }
            Console.WriteLine($"[ui] {(shapeOk ? "PASS" : "FAIL")} {belt.Key}: decompose {approved.Count} tarefas");
            if (!shapeOk) return 1;

            if (!build)
                return 0; // Plan: aceite descritivo, sem ato executivo.

            // Build: aprova tudo exigindo tools + executa + sintetiza em separado (protocolo do swap).
            var payload = approved.Select(t => new
            {
                title = t.Title,
                prompt = t.Prompt,
                etaMin = t.EtaMin,
                needsTools = true,
            }).ToArray();
            await PostAsync(api, "Chat/OrchestrateApproved",
                JsonSerializer.Serialize(new { sessionId, tasks = payload, synthesize = false }), "taskCount");
            bool done = await WaitTasksDoneAsync(api, web, sessionId, approved.Count, belt.TimeoutMin);
            Console.WriteLine($"[ui] {(done ? "PASS" : "FAIL")} {belt.Key}: {approved.Count} tarefas executadas");
            if (!done) return 1;

            string synthesis = await PostAsync(api, "Chat/Synthesize",
                JsonSerializer.Serialize(new { sessionId }), "synthesis");
            bool synthOk = synthesis.Length > 100;
            Console.WriteLine($"[ui] {(synthOk ? "PASS" : "FAIL")} {belt.Key}: sintese {synthesis.Length} chars");
            return synthOk ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ui] FAIL {belt.Key}: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
            return 1;
        }
    }

    // POST JSON autenticado (cookie do browser) com extrato de campo camelCase.
    // Retry 1x em 500 (race transitória no Dapper sob polling concorrente).
    private static async Task<string> PostAsync(HttpClient api, string path, string body, string field)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            using var res = await api.PostAsync(path,
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            string text = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(text);
                var el = doc.RootElement;
                foreach (var part in field.Split('.'))
                    el = el.GetProperty(part);
                return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
            }
            if (attempt == 2)
                throw new InvalidOperationException(((int)res.StatusCode) + " " + text[..Math.Min(200, text.Length)]);
            Console.WriteLine($"[ui] retry POST {path} ({attempt})");
            await Task.Delay(5000);
        }
        throw new InvalidOperationException("inalcançável");
    }

    // Poll ate todas done (ou timeout por faixa): painel /Tasks + marcadores nas mensagens
    // (sessoes plan/execute nao tem mensagem de decompose; o painel volta vazio).
    private static async Task<bool> WaitTasksDoneAsync(HttpClient api, string web, int sessionId, int total, int timeoutMin)
    {
        var deadline = DateTime.UtcNow.AddMinutes(timeoutMin);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var res = await api.GetAsync($"Chat/Sessions/{sessionId}/Tasks");
                string raw = await res.Content.ReadAsStringAsync();
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(raw);
                var list = doc.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
                int done = list.Count(t => t.TryGetProperty("status", out var s) && s.GetString() == "done");
                if (list.Length > 0)
                {
                    Console.WriteLine($"[ui] poll sessao {sessionId}: {done}/{list.Length} done");
                    if (done >= list.Length && list.Length > 0) return true;
                }
                else
                {
                    done = await CountDoneAsync(api, sessionId);
                    Console.WriteLine($"[ui] poll sessao {sessionId}: {done}/{total} concluidas (mensagens)");
                    if (done >= total && total > 0) return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ui] poll sessao {sessionId}: {ex.Message.Split('\n')[0]}");
            }
            await Task.Delay(TimeSpan.FromSeconds(60));
        }
        return false;
    }

    // Conta [Executor i] Concluído nas mensagens (fonte da verdade p/ sessoes sem decompose).
    private static async Task<int> CountDoneAsync(HttpClient api, int sessionId)
    {
        using var res = await api.GetAsync($"Chat/Sessions/{sessionId}/Messages?limit=200");
        string raw = await res.Content.ReadAsStringAsync();
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.EnumerateArray()
            .Count(m => m.TryGetProperty("role", out var r) && r.GetString() == "assistant"
                && (m.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "").Contains("Concluído"));
    }
}
