using System.Text.Json;
using Microsoft.Playwright;

namespace Rochas.OpenCodeBridge.Web.Test.Ui;

// E2E do comportamento da UI do chat (sem queimar GPU à toa):
// render do histórico (bolhas agente/sub, thinking 100%), ENVIAR->CANCELAR,
// Stop por sessão, % do thinking ao vivo e label de tool em execução.
// Roda com: dotnet run --project Rochas.OpenCodeBridge.Web.Test -c Release -- --ui-behavior
public static class ChatBehaviorTests
{
    private const string Email = "admin@pccs.com.br";
    private const string Password = "e2e";

    private static int _failures;
    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failures++;
    }

    public static async Task<int> RunAsync(string web)
    {
        _failures = 0;
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = "firefox",
            Headless = true,
        });
        var ctx = await browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        await page.GotoAsync(web + "/Account/Login");
        await page.FillAsync("input[name=email]", Email);
        await page.FillAsync("input[name=password]", Password);
        await page.ClickAsync("form button:has-text('Entrar')");
        await page.WaitForURLAsync(u => !u.Contains("/Account/Login"), new PageWaitForURLOptions { Timeout = 15000 });
        await page.GotoAsync(web + "/Chat");
        await page.WaitForSelectorAsync("#conv", new PageWaitForSelectorOptions { Timeout = 15000 });

        await HistoryRender(page);
        await CancelPerSession(page);
        await LiveThinkingAndTool(page);

        Console.WriteLine(_failures == 0 ? "UIBEHAVIOR PASS" : "UIBEHAVIOR FAIL");
        return _failures;
    }

    // Histórico: bolha do agente + cards de sub + thinking 100% em ambas.
    // Histórico: bolha do agente + cards de sub + thinking 100% em ambas.
    // Guiado a dados (sem IA): usa a sessão mais recente que tenha subagente.
    private static async Task HistoryRender(IPage page)
    {
        int sid = await page.EvaluateAsync<int>(@"async () => {
          const list = await (await fetch('/Chat/Sessions')).json();
          for (const s of list) {
            const msgs = await (await fetch(`/Chat/Sessions/${s.id}/Messages`)).json();
            if (msgs.some(m => m.role === 'subagent' && (m.content || '').trim())) return s.id;
          }
          return 0;
        }");
        if (sid == 0) { Console.WriteLine("SKIP hist: sem sessao com subagente"); return; }
        await page.EvaluateAsync("(id) => selectSession(id, 1, null)", sid);
        await page.WaitForTimeoutAsync(2500);
        var info = await page.EvaluateAsync<JsonElement>(@"() => {
          const conv = document.getElementById('conv');
          const heads = [...conv.querySelectorAll('.card-header p')].map(e => e.textContent);
          const sums = [...conv.querySelectorAll('details summary')].map(e => e.textContent);
          return { heads: heads, sums: sums,
            subs: conv.querySelectorAll('pre.subans').length };
        }");
        var heads = info.GetProperty("heads").EnumerateArray().Select(e => e.GetString()).ToList();
        var sums = info.GetProperty("sums").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        Check(heads.Contains("Agente"), "hist: bolha agente");
        Check(heads.Contains("Sub Agente"), "hist: bolha subagente");
        Check(info.GetProperty("subs").GetInt32() >= 1, "hist: card de sub renderiza texto");
        Check(sums.Count > 0 && sums.All(s => s.Contains("100%")), "hist: thinking 100% em ambas");
    }

    // ENVIAR inativa e mostra CANCELAR; CANCELAR posta o Stop DA sessão.
    private static async Task CancelPerSession(IPage page)
    {
        int sid = await page.EvaluateAsync<int>(@"async () => {
          const r = await fetch('/Chat/Sessions', { method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ agentId: 1, title: 'UICANCEL' }) });
          return (await r.json()).id;
        }");
        await page.EvaluateAsync("(id) => selectSession(id, 1, null)", sid);
        await page.WaitForTimeoutAsync(2000);
        await page.FillAsync("#prompt", "Responda somente com a palavra: ok");
        var stopReq = page.WaitForRequestAsync("**/Chat/Stop");
        await page.ClickAsync("#send");
        await page.WaitForTimeoutAsync(1500);
        var sendDisabled = await page.EvaluateAsync<bool>("() => document.getElementById('send').disabled");
        var cancelVisible = await page.EvaluateAsync<bool>("() => !!document.getElementById('cancelBtn') && document.getElementById('cancelBtn').style.display !== 'none'");
        Check(sendDisabled, "enviar: ENVIAR inativo durante stream");
        Check(cancelVisible, "enviar: CANCELAR visivel durante stream");
        await page.ClickAsync("#cancelBtn");
        var req = await stopReq;
        string body = req.PostData ?? "";
        Check(body.Contains(sid.ToString()), "cancelar: Stop da sessao");
        await page.WaitForTimeoutAsync(1000);
        var reenabled = await page.EvaluateAsync<bool>("() => !document.getElementById('send').disabled && !document.getElementById('cancelBtn')");
        Check(reenabled, "cancelar: UI destravada");
    }

    // Ao vivo: % do thinking progride e fecha em 100%; label de tool aparece e resolve.
    private static async Task LiveThinkingAndTool(IPage page)
    {
        int sid = await page.EvaluateAsync<int>(@"async () => {
          const r = await fetch('/Chat/Sessions', { method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ agentId: 1, title: 'UILIVE' }) });
          return (await r.json()).id;
        }");
        await page.EvaluateAsync("(id) => selectSession(id, 1, null)", sid);
        await page.WaitForTimeoutAsync(2000);
        await page.FillAsync("#prompt", "Use a tool glob para listar os .cs de Services e responda somente a quantidade. Nao delegue a subagentes.");
        await page.ClickAsync("#send");
        var seen = new HashSet<string>();
        bool toolLabel = false;
        string state = "";
        for (int i = 0; i < 110; i++)
        {
            await page.WaitForTimeoutAsync(3000);
            var sums = await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('#conv details summary')].map(s => s.textContent).join('|')");
            foreach (var p in sums.Split('|')) if (p.Contains("Racioc")) seen.Add(p.Trim());
            toolLabel |= await page.EvaluateAsync<bool>(@"() => [...document.querySelectorAll('#conv .tool-status')].some(e => (e.textContent || '').includes('glob'))");
            state = await page.EvaluateAsync<string>(@"() => document.getElementById('engineTxt')?.textContent || '?';");
            if (state.Contains("Conclu") || state.Contains("Interromp") || state.Contains("Desconect")) break;
        }
        Check(seen.Count > 0, "live: thinking com %");
        Check(seen.Any(s => s == "Raciocínio 100%"), "live: thinking fecha 100%");
        Check(toolLabel, "live: label de tool em execucao");
        Check(state.Contains("Conclu"), "live: stream conclui");
    }
}
