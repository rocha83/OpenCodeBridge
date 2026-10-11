namespace Rochas.OpenCodeBridge.Web.Test.Ui;

// Garante navegador p/ webdriver sem depender de pwsh/manual.
// Tenta o Firefox do Playwright (baixa via CLI oficial, idempotente);
// se a plataforma não suportar, cai p/ o Chrome do sistema (canal, sem download).
public static class PlaywrightSetup
{
    public static string EnsureBrowser()
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "ms-playwright");
        try
        {
            if (Directory.Exists(cache) &&
                Directory.GetDirectories(cache, "firefox-*")
                    .Any(d => File.Exists(Path.Combine(d, "firefox", "firefox"))))
                return "firefox";
        }
        catch { /* cache ilegível: tenta instalar mesmo assim */ }
        try
        {
            if (Microsoft.Playwright.Program.Main(new[] { "install", "firefox" }) == 0)
                return "firefox";
        }
        catch { /* plataforma sem Firefox: fallback abaixo */ }
        return "chrome";
    }
}
