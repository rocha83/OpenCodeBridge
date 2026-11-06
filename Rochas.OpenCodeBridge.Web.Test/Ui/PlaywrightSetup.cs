using Microsoft.Playwright;

namespace Rochas.OpenCodeBridge.Web.Test.Ui;

// Garante navegador p/ webdriver sem depender de pwsh/manual.
// Ordem: override do operador (PW_BROWSER) -> firefox ja' no cache do Playwright ->
// navegador do SISTEMA (Chrome, sem download) -> baixa o firefox como ultimo recurso.
//
// Medido em 2026-10-30: o cache estava vazio e o `install firefox` (download de ~90MB)
// era tentado ANTES de olhar o Chrome instalado; o smoke ficou minutos sem imprimir
// nada e a janela nunca abriu. Navegador do sistema primeiro = webdriver instantaneo
// e headed de verdade.
public static class PlaywrightSetup
{
    // HEADED=1: navegador visível (acompanha ao vivo); padrão headless.
    public static bool Headless() =>
        Environment.GetEnvironmentVariable("HEADED") != "1";

    /// <summary>
    /// Mantém a janela aberta AO FIM do teste, esperando o operador matar o processo
    /// (<c>PW_HOLD=1</c>). É opt-in de propósito: em bateria (WAR) o hold infinito
    /// travava o step até o timeout — medido em 2026-10-30. Headed sozinho já deixa a
    /// janela visível durante os testes, que é o acompanhamento pedido.
    /// </summary>
    /// <returns>True quando o operador pediu para segurar a janela no fim.</returns>
    public static bool Hold() => Environment.GetEnvironmentVariable("PW_HOLD") == "1";

    /// <summary>
    /// Perfil BUILD no dropdown <c>select#agentId</c> — o perfil dos units da
    /// esteira, obrigatório nos testes da Web UI que EXECUTAM tools. A UI nasce no
    /// primeiro agente do seed (MoE/Plan, só fetch/display/subagent) e o WAR mediria
    /// um perfil que não é o da esteira — medido em 2026-10-30: turno sem tool e
    /// nota falsa de "sem executar nada". Seleção por rótulo+modelo (Build Q4 :4124,
    /// o mesmo bridge dos units), nunca por id: o id do banco dev/PROD diverge.
    /// Subagente = MESMO tipo do chamador (Build Q4), mesma linha: só o Orch delega
    /// ao Scripter. Q4 nunca cruza com a GPU2/Q3 — a preferência descarta o par Q3.
    /// </summary>
    /// <param name="page">Página em <c>/Chat</c> com os dropdowns renderizados.</param>
    /// <param name="label">Rótulo do passo no log (faixa/smoke).</param>
    /// <returns><c>id|rótulo</c> do agente escolhido, para a evidência do teste.</returns>
    public static Task<string> SelectBuildAgentAsync(IPage page, string label) =>
        SelectAgentAsync(page, label, "Build (mode=build com a tool write)",
            "(o) => (o.dataset.mode || '').toLowerCase() === 'build' && (o.dataset.tools || '').includes('write')",
            "(o) => /Build/i.test(o.textContent) && !/Q3|GPU2/i.test(o.textContent)",
            pickSubagent: true,
            subPreferJs: "(o) => /Build/i.test(o.textContent || '') && !/Q3|GPU2/i.test(o.textContent || '')");

    /// <summary>
    /// Perfil PLAN no dropdown — para os testes da Web UI que NÃO usam tools: é o
    /// perfil com <c>thinking=events</c>, então o teste comprova o raciocínio
    /// aparecendo na bolha (reasoning na UI). Sem executor: chat simples.
    /// </summary>
    /// <param name="page">Página em <c>/Chat</c> com os dropdowns renderizados.</param>
    /// <param name="label">Rótulo do passo no log (faixa/smoke).</param>
    /// <returns><c>id|rótulo</c> do agente escolhido, para a evidência do teste.</returns>
    public static Task<string> SelectPlanAgentAsync(IPage page, string label) =>
        SelectAgentAsync(page, label, "Plan (reasoning na UI)",
            "(o) => (o.dataset.mode || '').toLowerCase() === 'plan'",
            "(o) => /Plan/i.test(o.textContent) && !/Q3|GPU2/i.test(o.textContent)",
            pickSubagent: false);

    /// <summary>
    /// Perfil ORCH no dropdown — para os testes da Web UI cujo enunciado pede
    /// DELEGAÇÃO (dividir em fases e compor com subagent). Orch é Build sem escrita
    /// (sem <c>write</c>/<c>exec</c>): planeja, delega e agrega, que é o comportamento
    /// medido na faixa. Orch é a ÚNICA exceção da regra de subagente: delega ao
    /// Scripter (Q4, mesma linha) — nunca à GPU2/Q3.
    /// </summary>
    /// <param name="page">Página em <c>/Chat</c> com os dropdowns renderizados.</param>
    /// <param name="label">Rótulo do passo no log (faixa/smoke).</param>
    /// <returns><c>id|rótulo</c> do agente escolhido, para a evidência do teste.</returns>
    public static Task<string> SelectOrchAgentAsync(IPage page, string label) =>
        SelectAgentAsync(page, label, "Orch (delegação, sem escrita)",
            "(o) => (o.dataset.mode || '').toLowerCase() === 'build' && (o.dataset.tools || '').includes('subagent') && !(o.dataset.tools || '').includes('write')",
            "(o) => /Orch/i.test(o.textContent) && !/Q3|GPU2/i.test(o.textContent)",
            pickSubagent: true,
            subPreferJs: "(o) => /Scripter/i.test(o.textContent || '') && !/Q3|GPU2/i.test(o.textContent || '')");

    /// <summary>
    /// Perfil MOE no dropdown — para o teste de TÓPICOS GERAIS, em que o generalista
    /// delega cada tema a um subagente DELE MESMO (MoE → MoE, mesma linha Q4). A
    /// cadeia de reserva (Scripter Q4 → Exec) só entra se não houver MoE na lista;
    /// Q4 nunca cruza com a GPU2/Q3.
    /// </summary>
    /// <param name="page">Página em <c>/Chat</c> com os dropdowns renderizados.</param>
    /// <param name="label">Rótulo do passo no log (faixa/smoke).</param>
    /// <returns><c>id|rótulo sub=id|rótulo</c> do agente e do subagente escolhidos.</returns>
    public static Task<string> SelectMoeAgentAsync(IPage page, string label) =>
        SelectAgentAsync(page, label, "MoE (tópicos gerais, delega a si mesmo)",
            "(o) => (o.dataset.mode || '').toLowerCase() === 'plan' && /MoE/i.test(o.textContent || '')",
            "(o) => /MoE/i.test(o.textContent) && !/GPU2|Q3/i.test(o.textContent)",
            pickSubagent: true,
            subPreferJs: "(o) => /MoE/i.test(o.textContent || '') && !/Q3|GPU2/i.test(o.textContent || '')");

    // Núcleo da seleção: filtro + preferência declarados como JS, sem id do banco.
    // O agente é escolhido, o campo fica realçado pelo compasso do espectador e só
    // então o subagente é escolhido no segundo dropdown (mesmo compasso).
    // REGRA DO SUBAGENTE: mesmo tipo do chamador (Build→Build, Plan→Plan, MoE→MoE),
    // mesma linha (Q4 com Q4); a ÚNICA exceção é Orch→Scripter. A cadeia de reserva
    // (Scripter Q4 → Scripter → Exec) só cobre perfil sem par na lista.
    private static async Task<string> SelectAgentAsync(IPage page, string label, string what,
        string filterJs, string preferJs, bool pickSubagent, string? subPreferJs = null)
    {
        string agentJs = @"() => {
          const sel = document.getElementById('agentId');
          const opts = [...sel.options].filter(o => o.value);
          const want = opts.filter(FILTER);
          const pick = want.find(PREFER) || want[0];
          if (!pick) return '';
          sel.value = pick.value;
          sel.dispatchEvent(new Event('change'));
          return pick.value + '|' + pick.textContent.trim();
        }".Replace("FILTER", filterJs).Replace("PREFER", preferJs);
        string picked = await SelectWithPaceAsync(page, "select#agentId", agentJs);
        if (string.IsNullOrEmpty(picked))
            throw new InvalidOperationException($"dropdown sem perfil {what}");

        string sub = "nenhum";
        if (pickSubagent && await page.QuerySelectorAsync("select#executorId") is not null)
        {
            // Subagente: preferência do perfil (mesmo tipo do chamador; Orch→Scripter),
            // depois a cadeia da esteira (Scripter Q4 → Scripter → Exec). Q3 só entra
            // como Exec de última reserva — nunca cruza a linha Q4 com a GPU2.
            string subJs = @"() => {
              const ex = document.getElementById('executorId');
              const all = [...ex.options].filter(o => o.value);
              const chosen =
                   all.find(SUBPREFER)
                || all.find(o => /Scripter/i.test(o.textContent || '') && !/GPU2|Q3/i.test(o.textContent || ''))
                || all.find(o => /Scripter/i.test(o.textContent || ''))
                || all.find(o => /Exec/i.test(o.textContent || ''));
              if (!chosen) return '';
              ex.value = chosen.value;
              ex.dispatchEvent(new Event('change'));
              return chosen.value + '|' + chosen.textContent.trim();
            }".Replace("SUBPREFER", subPreferJs ?? "(() => false)");
            string subPicked = await SelectWithPaceAsync(page, "select#executorId", subJs);
            if (!string.IsNullOrEmpty(subPicked))
                sub = subPicked;
        }
        Console.WriteLine($"[ui] {label}: agente {what} = {picked} sub={sub}");
        return $"{picked} sub={sub}";
    }

    /// <summary>
    /// Escolhe a opção com compasso de tela: ABRE a lista (o popup nativo do
    /// <c>&lt;select&gt;</c> não abre por script — expandir <c>size</c> mostra todas as
    /// opções e a marcada, que é o que o espectador precisa ver), espera, aplica a
    /// escolha, espera de novo e fecha a lista. Em headless apenas escolhe (0 ms).
    /// </summary>
    /// <param name="page">Página em <c>/Chat</c>.</param>
    /// <param name="selector">Dropdown alvo (<c>select#agentId</c>/<c>select#executorId</c>).</param>
    /// <param name="pickJs">JS que escolhe a opção e devolve <c>id|rótulo</c> (ou vazio).</param>
    /// <returns><c>id|rótulo</c> da opção escolhida, ou vazio quando não há perfil.</returns>
    private static async Task<string> SelectWithPaceAsync(IPage page, string selector, string pickJs)
    {
        int ms = PaceMs();
        if (ms <= 0) return await page.EvaluateAsync<string>(pickJs);

        // 1) lista aberta: size > 1 imita o popup e não fecha sozinho.
        await page.EvaluateAsync(@"sel => {
            const e = document.querySelector(sel);
            if (!e || e.tagName !== 'SELECT') return;
            e.dataset.paceSize = String(e.size);
            e.size = Math.max(2, Math.min(e.options.length, 9));
            e.style.height = 'auto';
            e.style.outline = '3px solid #f59e0b';
            e.style.outlineOffset = '2px';
            e.style.boxShadow = '0 0 0 4px rgba(245,158,11,.35)';
            e.scrollIntoView({ block: 'center' });
        }", selector);
        await page.WaitForTimeoutAsync(ms / 2);          // tempo para LER a lista

        string picked = await page.EvaluateAsync<string>(pickJs);
        await page.WaitForTimeoutAsync(ms);              // tempo para ver a MARCADA

        // 2) fecha a lista e limpa o realce (o app pode ter re-renderizado: tolera).
        await page.EvaluateAsync(@"sel => {
            const e = document.querySelector(sel);
            if (!e || e.tagName !== 'SELECT') return;
            const orig = parseInt(e.dataset.paceSize || '1', 10);
            e.size = orig > 1 ? orig : 1;
            e.style.height = '';
            e.style.outline = '';
            e.style.outlineOffset = '';
            e.style.boxShadow = '';
        }", selector);
        return picked;
    }

    /// <summary>
    /// Tempo do compasso em ms: <c>UI_PACE_MS</c> manda; sem a variável, 2500 com a
    /// janela aberta (espectador — a lista fica ~1,25 s aberta e a opção marcada
    /// ~2,5 s) e 0 em headless (CI sem plateia).
    /// </summary>
    /// <returns>Milissegundos de espera por campo (0 = sem compasso).</returns>
    public static int PaceMs()
    {
        string? env = Environment.GetEnvironmentVariable("UI_PACE_MS");
        if (int.TryParse(env, out int v)) return Math.Clamp(v, 0, 10000);
        return Headless() ? 0 : 2500;
    }

    /// <summary>
    /// Navegador a usar: <c>PW_BROWSER=chrome|firefox</c> força; senão prefere o
    /// sistema (Chrome) e só baixa o Firefox quando não há nada instalado.
    /// </summary>
    /// <returns><c>chrome</c> (canal do sistema/Chromium) ou <c>firefox</c>.</returns>
    public static string EnsureBrowser()
    {
        string? forced = Environment.GetEnvironmentVariable("PW_BROWSER");
        if (forced is "chrome" or "chromium") return "chrome";
        if (forced is "firefox") return "firefox";

        // Firefox já baixado pelo Playwright: usa (não precisa de canal do sistema).
        if (HasPlaywrightFirefox(out _)) return "firefox";

        // Chrome do sistema (LMDE/Debian): canal "chrome" do Chromium, sem download.
        if (HasSystemChrome()) return "chrome";

        // Último recurso: baixa o Firefox (precisa de rede; pode demorar).
        try
        {
            if (Microsoft.Playwright.Program.Main(new[] { "install", "firefox" }) == 0
                && HasPlaywrightFirefox(out _))
                return "firefox";
        }
        catch { /* plataforma sem download: fica no canal do sistema abaixo */ }
        return "chrome";
    }

    /// <summary>Firefox do Playwright presente em <c>~/.cache/ms-playwright</c>?</summary>
    /// <param name="cache">Raiz do cache (para log).</param>
    /// <returns>True quando existe um diretório <c>firefox-*</c> com o binário.</returns>
    private static bool HasPlaywrightFirefox(out string cache)
    {
        cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "ms-playwright");
        try
        {
            return Directory.Exists(cache)
                && Directory.GetDirectories(cache, "firefox-*")
                    .Any(d => File.Exists(Path.Combine(d, "firefox", "firefox")));
        }
        catch { return false; }
    }

    /// <summary>Existe Chrome/Chromium instalado no sistema (canal sem download)?</summary>
    /// <returns>True quando algum binário conhecido está presente.</returns>
    private static bool HasSystemChrome()
    {
        string[] candidates =
        {
            "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable",
            "/opt/google/chrome/chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser"
        };
        return candidates.Any(File.Exists);
    }
}
