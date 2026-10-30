using System.Text.Json;
using Microsoft.Playwright;

namespace Rochas.OpenCodeBridge.Web.Test.Ui;

// Teste simples via Playwright (mesma dinâmica dos E2E das faixas):
// login livre -> /Chat -> agente 9B plan -> nova sessão -> envia prompt
// curto que exige subagent -> aguarda engine -> confere toolcalls via API.
public static class SimpleHelloTests
{
    private const string Prompt =
        "Crie um script hello world em C#. Uma fase so, delegue ao subagente.";

    public static async Task<int> RunAsync(string web)
    {
        // A janela headed pode ser fechada no meio (operador encerrando a janela ou
        // crash do renderer): o Playwright devolve TargetClosedException e o smoke
        // morria por motivo que nao e' o produto (medido em 2026-10-30, attempt 4 do
        // webui-integracao-topicos). Relanca UMA vez com o MESMO aceite; qualquer
        // outra falha continua sendo FAIL.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await RunOnceAsync(web);
            }
            catch (PlaywrightException ex) when (ex.GetType().Name.Contains("TargetClosed", StringComparison.Ordinal))
            {
                // TargetClosedException e' interno nesta versao do Playwright: o tipo
                // chega pelo nome (o filtro nao captura outras PlaywrightException).
                if (attempt == 2)
                {
                    Console.WriteLine("[simple] FAIL harness: navegador fechou 2x (janela encerrada)");
                    return 1;
                }
                Console.WriteLine("[simple] janela fechada no meio: relanco o navegador (1x)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[simple] FAIL harness: {ex.GetType().Name} {ex.Message.Split('\n')[0]}");
                return 1;
            }
        }
        return 1;
    }

    private static async Task<int> RunOnceAsync(string web)
    {
        {
            string browserKind = PlaywrightSetup.EnsureBrowser();
            using var playwright = await Playwright.CreateAsync();
            await using var browser = browserKind == "chrome"
                ? await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Channel = "chrome",
                    Headless = PlaywrightSetup.Headless(),
                })
                : await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Channel = "firefox",
                    Headless = PlaywrightSetup.Headless(),
                });
            var context = await browser.NewContextAsync();
            var page = await context.NewPageAsync();

            await page.GotoAsync(web + "/Account/Login");
            await page.FillAsync("input[name=email]", "admin@bridge.pcss.com.br");
            await page.FillAsync("input[name=password]", "123");
            await page.ClickAsync("form button:has-text('Entrar')");
            await page.WaitForURLAsync(url => !url.Contains("/Account/Login"),
                new PageWaitForURLOptions { Timeout = 15000 });
            await page.GotoAsync(web + "/Chat");
            await page.WaitForSelectorAsync("#conv, #prompt, select#agentId",
                new PageWaitForSelectorOptions { Timeout = 15000 });
            Console.WriteLine("[simple] PASS login + /Chat");

            await page.SelectOptionAsync("select#agentId", "1");
            await page.ClickAsync("#newSession");
            await page.WaitForFunctionAsync(
                "() => document.getElementById('conv') && document.getElementById('conv').children.length === 0",
                null, new PageWaitForFunctionOptions { Timeout = 30000 });
            await page.WaitForTimeoutAsync(1500);
            await page.FillAsync("#prompt", Prompt);
            await page.ClickAsync("#send");
            Console.WriteLine("[simple] prompt enviado, aguardando engine");

            bool done = false;
            var deadline = DateTime.UtcNow.AddMinutes(20);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    string txt = await page.InnerTextAsync("#engineTxt");
                    if (txt.Contains("Conclu", StringComparison.OrdinalIgnoreCase)
                        || txt.Contains("Desconect", StringComparison.OrdinalIgnoreCase))
                    { done = txt.Contains("Conclu"); break; }
                }
                catch { }
                await page.WaitForTimeoutAsync(10000);
            }

            var jar = new System.Net.CookieContainer();
            foreach (var c in await context.CookiesAsync())
                jar.Add(new System.Net.Cookie(c.Name, c.Value, c.Path ?? "/", c.Domain.TrimStart('.')));
            using var api = new HttpClient(new HttpClientHandler { CookieContainer = jar })
            {
                BaseAddress = new Uri(web + "/"),
                Timeout = TimeSpan.FromMinutes(2),
            };
            int calls = -1;
            try
            {
                using var sres = await api.GetAsync("Chat/Sessions");
                sres.EnsureSuccessStatusCode();
                using var sdoc = JsonDocument.Parse(await sres.Content.ReadAsStringAsync());
                int sid = sdoc.RootElement.EnumerateArray().First().GetProperty("id").GetInt32();
                using var tres = await api.GetAsync($"Chat/Sessions/{sid}/ToolCalls?limit=200");
                if (tres.IsSuccessStatusCode)
                    using (var tdoc = JsonDocument.Parse(await tres.Content.ReadAsStringAsync()))
                        calls = tdoc.RootElement.EnumerateArray().Count();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[simple] sem API ({ex.Message.Split('\n')[0]})");
            }
            bool ok = done && calls > 0;
            Console.WriteLine($"[simple] {(ok ? "PASS" : "FAIL")} hello: engineDone={done} toolcalls={calls}");
            if (PlaywrightSetup.Hold())
            {
                // PW_HOLD=1: janela fica aberta ao fim; encerre o processo p/ fechar.
                Console.WriteLine("[simple] HOLD: janela aberta; mate o processo para encerrar.");
                await Task.Delay(Timeout.InfiniteTimeSpan);
            }
            return ok ? 0 : 1;
        }
    }
}
