// Piloto AO VIVO da Web UI (modelo real, sem mock): reproduz como o usuário usa.
// 1) "boa noite" -> detecta loop de saudação/despedida (bolhas repetidas).
// 2) write no workspace -> card Sim/Não com rótulo, clica Sim, confere arquivo.
// Roda com: dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --ui-live
// HEADED=1 mostra o navegador.
using Microsoft.Playwright;

namespace Rochas.OpenCodeBridge.Web.Test.Ui;

public static class LiveLoopTests
{
    private const string Email = "admin@bridge.pcss.com.br";
    private const string Password = "123";
    private static int _failures;

    private static void Check(bool ok, string name, string? extra = null)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (extra is null ? "" : " :: " + extra));
        if (!ok) _failures++;
    }

    public static async Task<int> RunAsync(string web)
    {
        _failures = 0;
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "chrome",
            Headless = Environment.GetEnvironmentVariable("HEADED") != "1",
        });
        var ctx = await browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        page.PageError += (_, e) => Console.WriteLine("pageerror: " + e);
        await page.GotoAsync(web + "/Account/Login");
        await page.FillAsync("input[name=email]", Email);
        await page.FillAsync("input[name=password]", Password);
        await page.ClickAsync("form button:has-text('Entrar')");
        await page.WaitForURLAsync(u => !u.Contains("/Account/Login"), new PageWaitForURLOptions { Timeout = 15000 });
        await page.GotoAsync(web + "/Chat");
        await page.WaitForSelectorAsync("#conv", new PageWaitForSelectorOptions { Timeout = 15000 });

        // Agente Build com write (senão o teste de Sim/Não não faz sentido).
        string agent = await page.EvaluateAsync<string>(@"() => {
          const sel = document.getElementById('agentId');
          const opts = [...sel.options];
          const build = opts.find(o => (o.dataset.mode || '').toLowerCase() === 'build' && (o.dataset.tools || '').includes('write')) || opts[0];
          sel.value = build.value; sel.dispatchEvent(new Event('change'));
          return build.value + '|' + build.text;
        }");
        Console.WriteLine("agente: " + agent);

        await page.ClickAsync("#newSession");
        await page.WaitForFunctionAsync("() => { try { return !!localStorage.getItem('bridgeSessionId'); } catch { return false; } }", null, new PageWaitForFunctionOptions { Timeout = 15000 });
        string sid = await page.EvaluateAsync<string>("() => localStorage.getItem('bridgeSessionId')");
        Console.WriteLine("sessao: " + sid);

        // Fase A: saudação (regressão do loop "boa noite... Encerrando...").
        await page.FillAsync("#prompt", "boa noite");
        await page.ClickAsync("#send");
        var snap = await PollBubbles(page, 150);
        Console.WriteLine($"saudacao: {snap.Count} bolhas em 150s");
        foreach (var b in snap.Take(12)) Console.WriteLine("  bolha: " + Trim(b, 160));
        int greet = snap.Count(t => t.Contains("Como posso ajudar"));
        int bye = snap.Count(t => t.Contains("Encerrando"));
        Check(greet < 3 && bye < 3, "live-sem-loop-saudacao", $"ajuda={greet} encerrando={bye} bolhas={snap.Count}");

        // Fase B: Sim/Não de verdade (write no workspace + clique Sim).
        await page.FillAsync("#prompt", "crie o arquivo loop-probe.txt com o conteúdo exato: sonda-ok");
        await page.ClickAsync("#send");
        string card = await WaitCard(page, 180);
        Console.WriteLine("card: " + Trim(card, 200));
        Check(card.Contains("escrever em arquivos"), "live-card-rotulo", Trim(card, 120));
        var simBtn = page.Locator("#conv details.tool-item button[data-ap='1']").Last;
        await simBtn.ClickAsync(new LocatorClickOptions { Timeout = 30000 });
        bool born = await WaitFile(page, sid, "loop-probe.txt", 120);
        Check(born, "live-sim-executa");
        if (born)
        {
            string content = await ReadFile(page, sid, "loop-probe.txt");
            Check(content.Contains("sonda-ok"), "live-conteudo", Trim(content, 80));
        }
        CleanupProbe();
        Console.WriteLine(_failures == 0 ? "[ui-live] PASS" : $"[ui-live] FAIL={_failures}");
        return _failures;
    }

    private static string Trim(string s, int n) =>
        (s ?? "").Replace("\n", " ").Trim() is string t ? (t.Length > n ? t[..n] + "…" : t) : "";

    private static async Task<List<string>> PollBubbles(IPage page, int seconds)
    {
        var seen = new List<string>();
        for (int i = 0; i < seconds / 5; i++)
        {
            await Task.Delay(5000);
            var heads = await page.EvaluateAsync<string[]>(@"() => [...document.querySelectorAll('#conv .card-body')].map(e => (e.innerText || '').trim()).filter(t => t.length > 0)");
            if (heads.Length != seen.Count) { seen = heads.ToList(); }
            // Fluxo parou (botão voltou a ENVIAR) + tempo p/ última bolha assentar.
            bool idle = await page.EvaluateAsync<bool>(@"() => document.getElementById('send')?.textContent?.includes('ENVIAR') ?? true");
            if (idle && i > 2) { await Task.Delay(3000); break; }
        }
        return seen;
    }

    private static async Task<string> WaitCard(IPage page, int seconds)
    {
        for (int i = 0; i < seconds / 3; i++)
        {
            var t = await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('#conv details.tool-item summary')].map(e => e.innerText).join(' || ')");
            if (t.Contains("você permite")) return t;
            await Task.Delay(3000);
        }
        return await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('#conv details.tool-item summary')].map(e => e.innerText).join(' || ')");
    }

    private static async Task<bool> WaitFile(IPage page, string sid, string name, int seconds)
    {
        for (int i = 0; i < seconds / 3; i++)
        {
            var j = await page.EvaluateAsync<string>(@"(s) => fetch(`/Chat/Sessions/${s}/ChangedFiles`).then(r => r.text()).catch(() => '')", sid);
            if (j.Contains(name)) return true;
            await Task.Delay(3000);
        }
        return false;
    }

    private static async Task<string> ReadFile(IPage page, string sid, string name) =>
        await page.EvaluateAsync<string>(@"(a) => fetch(`/Chat/Sessions/${a[0]}/File?path=${encodeURIComponent(a[1])}`).then(r => r.text()).catch(() => '')", new[] { sid, name });

    private static void CleanupProbe()
    {
        try
        {
            string p = Path.Combine(AppContext.BaseDirectory, "loop-probe.txt");
            if (File.Exists(p)) File.Delete(p);
        }
        catch { }
    }
}
