using System.Text.Json.Nodes;
using Microsoft.Playwright;

namespace Rochas.OpenCodeBridge.Web.Test.Ui;

// Frontpipe - medicao dos estilos computados da pagina gerada (mesmo padrao das
// belts: Playwright C# com Firefox do Playwright e fallback para o Chrome do sistema).
// Modo de linha de comando do Web.Test: --front-measure --front-url URL --front-out FILE
// Grava um snapshot JSON (getComputedStyle dos alvos + DOM + erros de console) que a
// suite da bridge (Rochas.OpenCodeBridge.Test/E2EFrontPipeTests) consome nos asserts.
public static class FrontMeasure
{
    // Alvos e propriedades medidas (espelha o contrato dos enunciados do frontpipe).
    private const string MeasureJs = @"() => {
      const specs = [
        ['#titulo', 'font-size,font-weight,color'],
        ['#quadro', 'width,height,padding-left,background-color,border-top-width'],
        ['#root',   'display'],
      ];
      const styles = {};
      for (const [sel, props] of specs) {
        const el = document.querySelector(sel);
        if (!el) { styles[sel] = null; continue; }
        const cs = getComputedStyle(el);
        const o = {};
        for (const p of props.split(',')) o[p] = cs.getPropertyValue(p).trim();
        const r = el.getBoundingClientRect();
        o.rect_w = +r.width.toFixed(2);
        o.rect_h = +r.height.toFixed(2);
        o.text = (el.textContent || '').trim().slice(0, 120);
        o.class = String(el.className || '');
        styles[sel] = o;
      }
      const root = document.querySelector('#root');
      return JSON.stringify({
        styles,
        dom: {
          root_children: root ? root.children.length : -1,
          title: document.title,
          body_chars: document.body ? document.body.innerText.length : -1,
        },
        links: [...document.querySelectorAll('link[rel=\""stylesheet\""], script[src]')]
          .map(n => n.getAttribute('href') || n.getAttribute('src')),
      });
    }";

    public static async Task<int> RunAsync(string url, string outFile)
    {
        string browserKind = PlaywrightSetup.EnsureBrowser();
        var consoleErrors = new List<string>();
        int status = 0;
        string error = "";
        JsonNode? snap = null;
        string browserUsed = browserKind;

        try
        {
            using var playwright = await Playwright.CreateAsync();
            var launch = new BrowserTypeLaunchOptions
            {
                Channel = browserKind == "chrome" ? "chrome" : "firefox",
                Headless = PlaywrightSetup.Headless(),
            };
            await using var browser = browserKind == "chrome"
                ? await playwright.Chromium.LaunchAsync(launch)
                : await playwright.Firefox.LaunchAsync(launch);
            var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            });
            page.PageError += (_, e) => consoleErrors.Add("pageerror: " + e.Split('\n')[0]);
            page.Console += (_, m) =>
            {
                if (m.Type == "error") consoleErrors.Add("console: " + m.Text.Split('\n')[0]);
            };

            var resp = await page.GotoAsync(url, new PageGotoOptions { Timeout = 60000, WaitUntil = WaitUntilState.NetworkIdle });
            status = resp?.Status ?? 0;
            await page.WaitForSelectorAsync("#root", new PageWaitForSelectorOptions { Timeout = 30000 });
            // Tailwind Play CDN compila as classes em runtime: da tempo de aplicar.
            await page.WaitForTimeoutAsync(2500);
            string raw = await page.EvaluateAsync<string>(MeasureJs);
            snap = JsonNode.Parse(raw);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
        }

        var outObj = new JsonObject
        {
            ["url"] = url,
            ["http_status"] = status,
            ["error"] = error,
            ["browser"] = browserUsed,
            ["viewport"] = new JsonObject { ["w"] = 1280, ["h"] = 900 },
            ["measured_at"] = DateTime.UtcNow.ToString("o"),
        };
        if (snap is JsonObject so)
        {
            foreach (var kv in so) outObj[kv.Key] = kv.Value?.DeepClone();
        }
        outObj["console_errors"] = new JsonArray(consoleErrors.Select(e => (JsonNode)JsonValue.Create(e)).ToArray());

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)) ?? ".");
        File.WriteAllText(outFile, outObj.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        bool ok = error.Length == 0 && status == 200 && outObj["dom"]?["root_children"]?.GetValue<int>() > 0;
        Console.WriteLine($"[front-measure] {(ok ? "PASS" : "FAIL")} {url} browser={browserUsed} status={status} " +
            $"titulo={outObj["styles"]?["#titulo"]?["font-size"]} quadro_w={outObj["styles"]?["#quadro"]?["width"]} " +
            $"erros={consoleErrors.Count}{(error.Length > 0 ? " erro=" + error : "")} -> {outFile}");
        return ok ? 0 : 1;
    }
}
