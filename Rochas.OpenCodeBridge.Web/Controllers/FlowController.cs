using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// Flow: controles de repeticao + calda de system prompt (so admin).
// Espelha o aspecto do CRUD de agentes. Comentarios pt-BR.
[Authorize]
public sealed class FlowController(
    IGenericRepository<PromptControl> controls,
    IPersistenceRepository<PromptControl> controlsWrite,
    PromptControlService controlCache) : Controller
{
    private bool IsAdmin => (User.FindFirst("IsAdmin")?.Value ?? "false").ToLower() == "true";

    public async Task<IActionResult> Index(string? criteria = null)
    {
        if (!IsAdmin) return Forbid();
        ViewBag.Criteria = criteria ?? "";
        // Filtro em memoria (Search faz AND entre colunas; aqui vale OR chave/rotulo).
        var all = await controls.Query(new PromptControl());
        IEnumerable<PromptControl> list = all;
        if (!string.IsNullOrWhiteSpace(criteria))
        {
            string q = criteria.Trim();
            list = all.Where(c => (c.Key ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (c.Label ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        ViewBag.Total = all.Count;
        ViewBag.Bridge = BridgeInfo();
        return View(list.OrderBy(c => c.Key).ToList());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string key, string value)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(key)) return RedirectToAction("Index");
        var current = await controls.Get(new PromptControl { Key = key });
        if (current is null) return RedirectToAction("Index");
        current.Value = value ?? "";
        current.UpdatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        await controlsWrite.Update(current, new PromptControl { Key = key });
        controlCache.Invalidate();
        TempData["FlowMsg"] = key.StartsWith("anti_repeat", StringComparison.OrdinalIgnoreCase)
            ? $"Salvo ({key}). Vai ao appsettings da bridge e exige reiniciá-la."
            : $"Salvo ({key}).";
        if (key.StartsWith("anti_repeat", StringComparison.OrdinalIgnoreCase))
            TempData["FlowMsg"] += " " + SyncBridgeSettings();
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RestartBridge()
    {
        if (!IsAdmin) return Forbid();
        TempData["FlowMsg"] = RestartBridge4124();
        return RedirectToAction("Index");
    }

    // Localiza a bridge :4124 (dotnet com --port 4124) e resume cmdline+cwd.
    private static (int pid, string cwd, string[] args)? FindBridge()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("dotnet"))
            {
                string cmdline;
                try { cmdline = System.IO.File.ReadAllText($"/proc/{p.Id}/cmdline").Replace('\0', ' ').Trim(); }
                catch { continue; }
                if (!cmdline.Contains("Rochas.OpenCodeBridge.dll") || !cmdline.Contains("--port 4124")) continue;
                string cwd;
                try { cwd = new System.IO.DirectoryInfo($"/proc/{p.Id}/cwd").LinkTarget ?? ""; }
                catch { cwd = ""; }
                var args = cmdline.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
                return (p.Id, cwd, args);
            }
        }
        catch { }
        return null;
    }

    private static string BridgeAppSettings(string cwd)
    {
        var dir = string.IsNullOrEmpty(cwd)
            ? "/usr/src/opencodebridge/Rochas.OpenCodeBridge/bin/Release/net10.0"
            : cwd;
        return System.IO.Path.Combine(dir, "appsettings.json");
    }

    private static Dictionary<string, string> ReadBridgeSettings()
    {
        var found = FindBridge();
        var path = BridgeAppSettings(found?.cwd ?? "");
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["_path"] = path };
        try
        {
            if (!System.IO.File.Exists(path)) return dict;
            var cfg = JsonNode.Parse(System.IO.File.ReadAllText(path))?.AsObject();
            if (cfg is null) return dict;
            foreach (var k in new[] { "antiRepeat", "antiRepeatWindow", "antiRepeatNotice" })
                dict[k] = cfg[k]?.ToString() ?? "(padrao)";
        }
        catch { }
        return dict;
    }

    private string BridgeInfo()
    {
        var found = FindBridge();
        var cfg = ReadBridgeSettings();
        return found is null
            ? $"bridge :4124 nao encontrada. appsettings: {cfg["_path"]}"
            : $"bridge :4124 pid {found.Value.pid} no ar. appsettings: {cfg["_path"]} " +
              $"(antiRepeat={cfg.GetValueOrDefault("antiRepeat")}, window={cfg.GetValueOrDefault("antiRepeatWindow")})";
    }

    // Espelha anti_repeat_* no appsettings.json da bridge (ela le no boot).
    private string SyncBridgeSettings()
    {
        try
        {
            var found = FindBridge();
            var path = BridgeAppSettings(found?.cwd ?? "");
            JsonObject cfg;
            try { cfg = JsonNode.Parse(System.IO.File.ReadAllText(path))?.AsObject() ?? new JsonObject(); }
            catch { cfg = new JsonObject(); }
            var all = controls.Query(new PromptControl()).GetAwaiter().GetResult()
                .ToDictionary(c => c.Key ?? "", c => c.Value ?? "", StringComparer.OrdinalIgnoreCase);
            if (all.TryGetValue("anti_repeat_enabled", out string? en))
                cfg["antiRepeat"] = en.Trim().ToLowerInvariant() is "true" or "1";
            if (all.TryGetValue("anti_repeat_window", out string? w) && int.TryParse(w, out int wi))
                cfg["antiRepeatWindow"] = Math.Clamp(wi, 64, 2048);
            if (all.TryGetValue("anti_repeat_notice", out string? n) && (n ?? "").Length >= 20)
                cfg["antiRepeatNotice"] = n;
            System.IO.File.WriteAllText(path, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return $"appsettings atualizado ({path}).";
        }
        catch (Exception ex) { return "falha ao atualizar appsettings: " + ex.Message; }
    }

    private static string RestartBridge4124()
    {
        var found = FindBridge();
        if (found is null) return "bridge :4124 nao encontrada; nada reiniciado.";
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                WorkingDirectory = string.IsNullOrEmpty(found.Value.cwd) ? "/tmp" : found.Value.cwd,
            };
            foreach (var a in found.Value.args) psi.ArgumentList.Add(a);
            psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            Process.GetProcessById(found.Value.pid).Kill();
            System.Threading.Thread.Sleep(2000);
            Process.Start(psi);
            return $"bridge reiniciada (pid antigo {found.Value.pid}). Aguarde o health :4124.";
        }
        catch (Exception ex) { return "falha ao reiniciar: " + ex.Message; }
    }
}
