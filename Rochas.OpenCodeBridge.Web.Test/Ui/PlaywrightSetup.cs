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
