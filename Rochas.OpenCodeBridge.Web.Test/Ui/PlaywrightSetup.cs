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
    /// A preferência descarta o par GPU2/Q3 — o perfil da esteira é o Q4.
    /// Subagente vai para o perfil Scripter Q4 (o executor real da esteira); o Exec
    /// da GPU2 é fallback e o MoE nunca é usado. Sem nenhum, fica "sub=nenhum".
    /// </summary>
    /// <param name="page">Página em <c>/Chat</c> com os dropdowns renderizados.</param>
    /// <param name="label">Rótulo do passo no log (faixa/smoke).</param>
    /// <returns><c>id|rótulo</c> do agente escolhido, para a evidência do teste.</returns>
    public static Task<string> SelectBuildAgentAsync(IPage page, string label) =>
        SelectAgentAsync(page, label, "Build (mode=build com a tool write)",
            "(o) => (o.dataset.mode || '').toLowerCase() === 'build' && (o.dataset.tools || '').includes('write')",
            "(o) => /Build/i.test(o.textContent) && !/Q3|GPU2/i.test(o.textContent)",
            pickSubagent: true);

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
    /// medido na faixa. O subagente vai para o Scripter (Q4) — o mesmo que a
    /// esteira usa para executar as fases; Exec (GPU2) é fallback, MoE nunca.
    /// </summary>
    /// <param name="page">Página em <c>/Chat</c> com os dropdowns renderizados.</param>
    /// <param name="label">Rótulo do passo no log (faixa/smoke).</param>
    /// <returns><c>id|rótulo</c> do agente escolhido, para a evidência do teste.</returns>
    public static Task<string> SelectOrchAgentAsync(IPage page, string label) =>
        SelectAgentAsync(page, label, "Orch (delegação, sem escrita)",
            "(o) => (o.dataset.mode || '').toLowerCase() === 'build' && (o.dataset.tools || '').includes('subagent') && !(o.dataset.tools || '').includes('write')",
            "(o) => /Orch/i.test(o.textContent) && !/Q3|GPU2/i.test(o.textContent)",
            pickSubagent: true);

    // Núcleo da seleção: filtro + preferência declarados como JS, sem id do banco.
    private static async Task<string> SelectAgentAsync(IPage page, string label, string what,
        string filterJs, string preferJs, bool pickSubagent)
    {
        string js = @"() => {
          const sel = document.getElementById('agentId');
          const opts = [...sel.options].filter(o => o.value);
          const want = opts.filter(FILTER);
          const pick = want.find(PREFER) || want[0];
          if (!pick) return '';
          sel.value = pick.value;
          sel.dispatchEvent(new Event('change'));
          let sub = '';
          const ex = document.getElementById('executorId');
          if (ex && PICKSUB) {
            const all = [...ex.options].filter(o => o.value);
            // Subagente da esteira = Scripter Q4 (quem executa as fases); o Exec da
            // GPU2 e' fallback. Nunca o MoE: ele so' fetch/display/subagent.
            const chosen =
                 all.find(o => /Scripter/i.test(o.textContent || '') && !/GPU2|Q3/i.test(o.textContent || ''))
              || all.find(o => /Scripter/i.test(o.textContent || ''))
              || all.find(o => /Exec/i.test(o.textContent || ''));
            if (chosen) {
              ex.value = chosen.value;
              ex.dispatchEvent(new Event('change'));
              sub = chosen.textContent.trim();
            }
          }
          return pick.value + '|' + pick.textContent.trim() + ' sub=' + (sub || 'nenhum');
        }".Replace("FILTER", filterJs).Replace("PREFER", preferJs)
          .Replace("PICKSUB", pickSubagent ? "true" : "false");
        string picked = await page.EvaluateAsync<string>(js);
        if (string.IsNullOrEmpty(picked))
            throw new InvalidOperationException($"dropdown sem perfil {what}");
        Console.WriteLine($"[ui] {label}: agente {what} = {picked}");
        return picked;
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
