using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Ui;

// E2E comportamental da UI via simulador SSE (sem GPU): bolhas agente/sub,
// thinking com % nas duas, labels de tools, ENVIAR->CANCELAR por sessão,
// fallback silencioso e erro mid-loop com DONE garantido.
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
        string browserKind = PlaywrightSetup.EnsureBrowser();
        Console.WriteLine("browser: " + browserKind);
        using var sim = new SseSimulator();
        using var pw = await Playwright.CreateAsync();
        await using var browser = browserKind == "chrome"
            ? await pw.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "chrome",
                Headless = true,
            })
            : await pw.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Channel = "firefox",
                Headless = true,
            });
        var ctx = await browser.NewContextAsync();
        var page = await ctx.NewPageAsync();
        var consoles = new List<string>();
        page.Console += (_, e) => { lock (consoles) { consoles.Add(e.Type + ": " + e.Text); } };
        page.PageError += (_, e) => { lock (consoles) { consoles.Add("pageerror: " + e); } };
        await page.GotoAsync(web + "/Account/Login");
        await page.FillAsync("input[name=email]", Email);
        await page.FillAsync("input[name=password]", Password);
        await page.ClickAsync("form button:has-text('Entrar')");
        await page.WaitForURLAsync(u => !u.Contains("/Account/Login"), new PageWaitForURLOptions { Timeout = 15000 });
        await page.GotoAsync(web + "/Chat");
        await page.WaitForSelectorAsync("#conv", new PageWaitForSelectorOptions { Timeout = 15000 });

        int mockAgent = await EnsureMockAgent(page, web, sim.BaseUrl);
        Check(mockAgent > 0, "mock: agente simulador criado");

        var sessions = new List<int>();
        try
        {
            await A_ThinkingPct(page, mockAgent, sessions);
            await B_ToolLabel(page, mockAgent, sessions, sim);
            int sidSub = await C_SubCard(page, mockAgent, sessions, sim);
            await HistoryRender(page, sidSub);
            await D_SilentFallback(page, mockAgent, sessions, sim);
            await E_Cancel(page, mockAgent, sessions, sim);
            await F_BridgeError(page, mockAgent, sessions, sim);
        }
        finally
        {
            if (_failures > 0)
            {
                Console.WriteLine("--- console do navegador (ultimas 30) ---");
                lock (consoles)
                    foreach (var c in consoles.TakeLast(30)) Console.WriteLine(c);
                try
                {
                    Console.WriteLine("engineTxt: " + await page.EvaluateAsync<string>(
                        "() => document.getElementById('engineTxt')?.textContent ?? '?';"));
                    Console.WriteLine("conv filhos: " + await page.EvaluateAsync<int>(
                        "() => document.getElementById('conv')?.children.length ?? -1;"));
                }
                catch { }
            }
            foreach (int sid in sessions)
                await page.EvaluateAsync("(id) => fetch(`/Chat/Sessions/${id}`, { method: 'DELETE' })", sid);
            await page.EvaluateAsync("(id) => fetch(`/Agents/Delete/${id}`)", mockAgent);
        }

        Console.WriteLine(_failures == 0 ? "UIBEHAVIOR PASS" : "UIBEHAVIOR FAIL");
        return _failures;
    }

    private static async Task<int> EnsureMockAgent(IPage page, string web, string bridgeUrl)
    {
        string html = await page.EvaluateAsync<string>(@"async () => {
          const form = new URLSearchParams({ Name: 'UITEST-MOCK', Model: 'mock-model',
            BridgeUrl: '__URL__', Temperature: '0.4', Thinking: 'events', Active: 'true',
            Role: 'orchestrator', Mode: 'plan', SystemPrompt: 'mock' });
          await fetch('/Agents/Create', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: form });
          return await (await fetch('/Agents')).text();
        }".Replace("__URL__", bridgeUrl));
        var ms = Regex.Matches(html, @"<td>UITEST-MOCK</td>.*?/Agents/Edit/(\d+)", RegexOptions.Singleline);
        int newest = 0;
        foreach (Match mm in ms) newest = Math.Max(newest, int.Parse(mm.Groups[1].Value));
        return newest;
    }

    private static async Task<int> NewSession(IPage page, int agentId, string title, List<int> sessions)
    {
        int sid = await page.EvaluateAsync<int>(@"async ({agentId, title}) => {
          const r = await fetch('/Chat/Sessions', { method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ agentId, title }) });
          return (await r.json()).id;
        }", new { agentId, title });
        sessions.Add(sid);
        await page.EvaluateAsync("(a) => selectSession(a.id, a.agentId, null)", new { id = sid, agentId });
        await page.WaitForTimeoutAsync(1500);
        return sid;
    }

    private static async Task<string> SendAndWait(IPage page, string text, int maxPolls = 40)
    {
        await page.FillAsync("#prompt", text);
        await page.ClickAsync("#send");
        string state = "";
        for (int i = 0; i < 60; i++)
        {
            await page.WaitForTimeoutAsync(1500);
            state = await page.EvaluateAsync<string>(@"() => document.getElementById('engineTxt')?.textContent || '?';");
            if (state.Contains("Conclu") || state.Contains("Interromp") || state.Contains("Desconect")) break;
        }
        return state;
    }

    // A: thinking com % progride e fecha 100%, texto na bolha, conclui.
    private static async Task A_ThinkingPct(IPage page, int agent, List<int> sessions)
    {
        int sid = await NewSession(page, agent, "UIMOCK-A", sessions);
        _ = sid;
        // roteiro vai direto na fila do mock (mesmo processo do simulador)
        MockBridgeClient.ClearScript();
        MockBridgeClient.EnqueueScript(new MockBridgeClient.MockTurn(
            new string('r', 800), "texto-mock-a", null, null, 5));
        await page.FillAsync("#prompt", "vai");
        await page.ClickAsync("#send");
        var seen = new HashSet<string>();
        string state = "";
        for (int i = 0; i < 60; i++)
        {
            await page.WaitForTimeoutAsync(1000);
            var sums = await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('#conv details summary')].map(s => s.textContent).join('|')");
            foreach (var p in sums.Split('|')) if (p.Contains("Racioc")) seen.Add(p.Trim());
            state = await page.EvaluateAsync<string>(@"() => document.getElementById('engineTxt')?.textContent || '?';");
            if (state.Contains("Conclu") || state.Contains("Interromp") || state.Contains("Desconect")) break;
        }
        Check(seen.Count >= 1 && seen.Any(s => Regex.IsMatch(s, @"Raciocínio \d+%")), "mock-a: thinking com %");
        Check(seen.Contains("Raciocínio 100%"), "mock-a: fecha 100%");
        Check(state.Contains("Conclu"), "mock-a: conclui");
    }

    private static async Task B_ToolLabel(IPage page, int agent, List<int> sessions, SseSimulator sim)
    {
        int sid = await NewSession(page, agent, "UIMOCK-B", sessions);
        _ = sid;
        sim.Script(
            new MockBridgeClient.MockTurn("pensando", "",
                new List<MockBridgeClient.MockToolCall> { new("glob", "{\"pattern\":\"**/*.cs\"}") }, null, 5),
            new MockBridgeClient.MockTurn("", "total-mock"));
        await page.FillAsync("#prompt", "vai");
        await page.ClickAsync("#send");
        bool label = false;
        string state = "";
        for (int i = 0; i < 60; i++)
        {
            await page.WaitForTimeoutAsync(1000);
            label |= await page.EvaluateAsync<bool>(@"() => [...document.querySelectorAll('#conv .tool-status')].some(e => (e.textContent || '').includes('glob'))
              || [...document.querySelectorAll('#conv details summary')].some(e => (e.textContent || '').includes('glob'))");
            state = await page.EvaluateAsync<string>(@"() => document.getElementById('engineTxt')?.textContent || '?';");
            if (state.Contains("Conclu") || state.Contains("Interromp") || state.Contains("Desconect")) break;
        }
        Check(label, "mock-b: label de tool em execucao");
        Check(state.Contains("Conclu"), "mock-b: conclui");
    }

    private static async Task<int> C_SubCard(IPage page, int agent, List<int> sessions, SseSimulator sim)
    {
        int sid = await NewSession(page, agent, "UIMOCK-C", sessions);
        sim.Script(
            new MockBridgeClient.MockTurn("planejando", "",
                new List<MockBridgeClient.MockToolCall> { new("subagent", "{\"prompt\":\"fase-mock\"}") }, null, 5),
            new MockBridgeClient.MockTurn("raciocinio-mock-sub", "script-mock-sub"),
            new MockBridgeClient.MockTurn("", "fechado"));
        string state = await SendAndWait(page, "vai");
        var info = await page.EvaluateAsync<JsonElement>(@"() => {
          const conv = document.getElementById('conv');
          const heads = [...conv.querySelectorAll('.card-header p')].map(e => e.textContent);
          const subs = [...conv.querySelectorAll('pre.subans')].map(e => e.textContent);
          return { heads: heads, subs: subs };
        }");
        var heads = info.GetProperty("heads").EnumerateArray().Select(e => e.GetString()).ToList();
        var subs = info.GetProperty("subs").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        Check(heads.Contains("Sub Agente"), "mock-c: card subagente");
        Check(subs.Any(s => s.Contains("script-mock-sub")), "mock-c: texto do sub");
        Check(state.Contains("Conclu"), "mock-c: conclui");
        return sid;
    }

    private static async Task HistoryRender(IPage page, int sid)
    {
        await page.EvaluateAsync("(id) => selectSession(id, 1, null)", sid);
        await page.WaitForTimeoutAsync(2500);
        var info = await page.EvaluateAsync<JsonElement>(@"() => {
          const conv = document.getElementById('conv');
          return { heads: [...conv.querySelectorAll('.card-header p')].map(e => e.textContent),
            sums: [...conv.querySelectorAll('details summary')].map(e => e.textContent),
            subs: conv.querySelectorAll('pre.subans').length };
        }");
        var heads = info.GetProperty("heads").EnumerateArray().Select(e => e.GetString()).ToList();
        var sums = info.GetProperty("sums").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        Check(heads.Contains("Agente"), "hist: bolha agente");
        Check(heads.Contains("Sub Agente"), "hist: bolha subagente");
        Check(info.GetProperty("subs").GetInt32() >= 1, "hist: card de sub");
        Check(sums.Count > 0 && sums.All(s => s.Contains("100%")), "hist: thinking 100%");
    }

    // D: fechamento silencioso diferencia sub de tool comum.
    private static async Task D_SilentFallback(IPage page, int agent, List<int> sessions, SseSimulator sim)
    {
        int sid = await NewSession(page, agent, "UIMOCK-D1", sessions);
        _ = sid;
        sim.Script(
            new MockBridgeClient.MockTurn("", "",
                new List<MockBridgeClient.MockToolCall> { new("glob", "{\"pattern\":\"**/*.cs\"}") }, null, 5),
            new MockBridgeClient.MockTurn());
        string state = await SendAndWait(page, "vai");
        var bubble = await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('#conv pre.ans')].pop()?.textContent ?? ''");
        Check(bubble.Contains("ferramentas solicitadas"), "mock-d1: fallback de tools");
        Check(state.Contains("Conclu"), "mock-d1: conclui");

        int sid2 = await NewSession(page, agent, "UIMOCK-D2", sessions);
        _ = sid2;
        sim.Script(
            new MockBridgeClient.MockTurn("", "",
                new List<MockBridgeClient.MockToolCall> { new("subagent", "{\"prompt\":\"f\"}") }, null, 5),
            new MockBridgeClient.MockTurn("", "x-sub"),
            new MockBridgeClient.MockTurn());
        state = await SendAndWait(page, "vai");
        bubble = await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('#conv pre.ans')].pop()?.textContent ?? ''");
        Check(bubble.Contains("tarefas delegadas"), "mock-d2: fallback de subs");
        Check(state.Contains("Conclu"), "mock-d2: conclui");
    }

    // E: CANCELAR posta o Stop DA sessão e destrava.
    private static async Task E_Cancel(IPage page, int agent, List<int> sessions, SseSimulator sim)
    {
        int sid = await NewSession(page, agent, "UIMOCK-E", sessions);
        sim.Script(new MockBridgeClient.MockTurn(new string('r', 3000), "nunca", null, null, 120));
        await page.FillAsync("#prompt", "vai");
        var stopReq = page.WaitForRequestAsync("**/Chat/Stop");
        await page.ClickAsync("#send");
        await page.WaitForTimeoutAsync(1500);
        Check(await page.EvaluateAsync<bool>("() => document.getElementById('send').disabled"), "cancel: ENVIAR inativo");
        Check(await page.EvaluateAsync<bool>("() => !!document.getElementById('cancelBtn')"), "cancel: CANCELAR visivel");
        await page.ClickAsync("#cancelBtn");
        var req = await stopReq;
        Check((req.PostData ?? "").Contains(sid.ToString()), "cancel: Stop da sessao");
        await page.WaitForTimeoutAsync(1000);
        Check(await page.EvaluateAsync<bool>("() => !document.getElementById('send').disabled"), "cancel: destrava");
        Check(await page.EvaluateAsync<bool>("() => (document.querySelector('#engineDot')?.className ?? '').includes('err')"), "cancel: bolinha vermelha");
    }

    // F: erro mid-loop vira texto na bolha + DONE (sem prender).
    private static async Task F_BridgeError(IPage page, int agent, List<int> sessions, SseSimulator sim)
    {
        int sid = await NewSession(page, agent, "UIMOCK-F", sessions);
        _ = sid;
        // turno 1 com tool (loop continua) + turno 2 com erro: cobre erro mid-loop.
        sim.Script(
            new MockBridgeClient.MockTurn("", "parcial",
                new List<MockBridgeClient.MockToolCall> { new("glob", "{\"pattern\":\"**/*.cs\"}") }, null, 5),
            new MockBridgeClient.MockTurn("", "", null, "boom-500"));
        string state = await SendAndWait(page, "vai");
        var bubble = await page.EvaluateAsync<string>(@"() => [...document.querySelectorAll('#conv pre.ans')].pop()?.textContent ?? ''");
        Check(bubble.Contains("Erro:"), "mock-f: erro na bolha");
        Check(state.Contains("Conclu"), "mock-f: DONE mesmo com erro");
    }
}
