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

    // Orquestrador 8B (id 1 build / id 6 plan) + executores (2 plan CPU, 4/5 build GPU).
    private const int BuildOrchestratorId = 1;
    private const int PlanOrchestratorId = 6;
    private const int PlanExecutorId = 2;

    private sealed record Belt(string Key, string Title, string Prompt, int ExecutorId, int TimeoutMin);

    private static readonly Belt[] Belts =
    {
        new("azul", "Faixa AZUL E2E",
            "Especifique um CRUD de reembolsos corporativos: modelo de dados com campos, regras de aprovacao por faixa de valor e os endpoints necessarios.",
            4, 30),
        new("verde", "Faixa VERDE E2E",
            "Defina as regras de validacao de CPF e CNPJ (digitos verificadores) e de 2 exemplos validos de cada.",
            4, 30),
        new("roxa", "Faixa ROXA E2E",
            "Defina validacao e mascaras para e-mail, telefone BR com DDD e CEP: regras, regex de cada um e 2 exemplos validos de cada.",
            5, 30),
        new("marrom", "Faixa MARROM E2E",
            "Especifique um CRUD de reembolsos corporativos: modelo de dados com campos, regras de aprovacao por faixa de valor, endpoints necessarios, e pipeline CI/CD com Docker (Dockerfile multi-stage e compose para subir api+db).",
            4, 90),
        new("preta", "Faixa PRETA E2E",
            "Arquitetura de ecossistema (divida em cerca de 14 subtarefas): Portal do Colaborador (ponto eletronico, reembolsos, organograma) integrado via barramento de eventos assincrono a outros sistemas (folha, ERP); APIs REST do portal; pipeline de Big Data com ML: regressao para previsao de gastos, classificacao de reembolsos suspeitos e rede neural (perceptron multicamadas) para deteccao de anomalias em ponto eletronico.",
            5, 120),
    };

    public static async Task<int> RunAsync(string web, string[] only, string mode)
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

        // Login livre (qualquer senha) + chega ao /Chat.
        await page.GotoAsync(web + "/Account/Login");
        await page.FillAsync("input[name=email]", "admin@mova.com");
        await page.FillAsync("input[name=password]", "e2e");
        await page.ClickAsync("form button:has-text('Entrar')");
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login"), new PageWaitForURLOptions { Timeout = 15000 });
        await page.GotoAsync(web + "/Chat");
        await page.WaitForSelectorAsync("#conv, #prompt, select#agentId", new PageWaitForSelectorOptions { Timeout = 15000 });
        Console.WriteLine("[ui] PASS login + /Chat (UA Win11, Firefox)");

        foreach (var belt in wanted)
            failures += await RunBeltAsync(page, web, belt, build);

        return failures;
    }

    private static async Task<int> RunBeltAsync(IPage page, string web, Belt belt, bool build)
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
                    sessionId = int.Parse(await FetchAsync(page, web, "/Chat/Sessions", "POST",
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

            // Decompose: 8B amplia o entendimento e segmenta em tarefas atomicas.
            string tasksJson = await FetchAsync(page, web, "/Chat/Decompose", "POST",
                JsonSerializer.Serialize(new { sessionId, text = belt.Prompt }), "tasks");
            using var tasksDoc = JsonDocument.Parse(tasksJson);
            var tasks = tasksDoc.RootElement.EnumerateArray().ToArray();
            bool shapeOk = tasks.Length is >= 4 and <= 16
                && tasks.All(t => t.TryGetProperty("title", out var ti) && ti.GetString()?.Length > 0
                    && t.TryGetProperty("prompt", out var pr) && pr.GetString()?.Length > 0);
            Console.WriteLine($"[ui] {(shapeOk ? "PASS" : "FAIL")} {belt.Key}: decompose {tasks.Length} tarefas");
            if (!shapeOk) return 1;

            if (!build)
                return 0; // Plan: aceite descritivo, sem ato executivo.

            // Build: aprova tudo exigindo tools + executa + sintetiza em separado (protocolo do swap).
            var approved = tasks.Select(t => new
            {
                title = t.GetProperty("title").GetString(),
                prompt = t.GetProperty("prompt").GetString(),
                etaMin = t.TryGetProperty("etaMin", out var e) ? e.GetDouble() : 3,
                needsTools = true,
            }).ToArray();
            await FetchAsync(page, web, "/Chat/OrchestrateApproved", "POST",
                JsonSerializer.Serialize(new { sessionId, tasks = approved, synthesize = false }), "taskCount");
            bool done = await WaitTasksDoneAsync(page, web, sessionId, tasks.Length, belt.TimeoutMin);
            Console.WriteLine($"[ui] {(done ? "PASS" : "FAIL")} {belt.Key}: {tasks.Length} tarefas executadas");
            if (!done) return 1;

            string synthesis = await FetchAsync(page, web, "/Chat/Synthesize", "POST",
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

    // Chama endpoint JSON no contexto da pagina (leva cookie de auth) e extrai um campo.
    private static async Task<string> FetchAsync(IPage page, string web, string path, string method, string body, string field)
    {
        string text = await FetchRawAsync(page, web, path, method, body);
        using var doc = JsonDocument.Parse(text);
        var el = doc.RootElement;
        foreach (var part in field.Split('.'))
            el = el.GetProperty(part);
        return el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
    }

    // Corpo bruto do endpoint (GET ou POST com body JSON).
    private static async Task<string> FetchRawAsync(IPage page, string web, string path, string method = "GET", string body = "null")
    {
        string js = $"fetch('{web}{path}', {{method:'{method}', headers:{{'Content-Type':'application/json'}}, body:{body}}})"
            + ".then(async r => {{ const t = await r.text(); if (!r.ok) throw new Error(r.status + ' ' + t.slice(0,200)); return t; }})";
        return await page.EvaluateAsync<string>(js);
    }

    // Poll do painel /Tasks ate todas done (ou timeout por faixa).
    private static async Task<bool> WaitTasksDoneAsync(IPage page, string web, int sessionId, int total, int timeoutMin)
    {
        var deadline = DateTime.UtcNow.AddMinutes(timeoutMin);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string raw = await FetchRawAsync(page, web, $"/Chat/Sessions/{sessionId}/Tasks");
                using var doc = JsonDocument.Parse(raw);
                var list = doc.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
                int done = list.Count(t => t.TryGetProperty("status", out var s) && s.GetString() == "done");
                Console.WriteLine($"[ui] poll sessao {sessionId}: {done}/{list.Length} done");
                if (list.Length >= total && done >= list.Length && list.Length > 0) return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ui] poll sessao {sessionId}: {ex.Message.Split('\n')[0]}");
            }
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        return false;
    }
}
