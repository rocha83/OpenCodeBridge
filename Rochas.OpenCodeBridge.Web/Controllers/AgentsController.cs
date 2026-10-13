using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// CRUD de agentes/modelos via GenericRepository. Comentarios pt-BR.
[Authorize]
public sealed class AgentsController(IGenericRepository<Agent> agents, IPersistenceRepository<Agent> agentsWrite) : Controller
{
    public async Task<IActionResult> Index()
    {
        ViewBag.NewAgent = DefaultAgent();
        return View(await agents.Query(new Agent()));
    }

    private static Agent DefaultAgent() =>
        new() { Active = true, Model = "qwen3.5-9b", BridgeUrl = "http://127.0.0.1:4124", Temperature = 0.4, Thinking = "events", Tools = "shell,read,write,edit,grep,glob,fetch,git" };

    // Presets do form -> tools reais (leitura=read+grep, escrita=write+edit).
    private static string ExpandTools(string[]? selected)
    {
        var want = new HashSet<string>(selected ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var out_ = new List<string>();
        if (want.Contains("glob")) out_.Add("glob");
        if (want.Contains("read")) { out_.Add("read"); out_.Add("grep"); }
        if (want.Contains("write")) { out_.Add("write"); out_.Add("edit"); }
        foreach (var t in new[] { "git", "fetch", "shell", "subagent" }) if (want.Contains(t)) out_.Add(t);
        if (want.Contains("display")) { out_.Add("read"); out_.Add("display"); }
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            { ["glob"] = 0, ["read"] = 1, ["grep"] = 2, ["write"] = 3, ["edit"] = 4, ["git"] = 5, ["fetch"] = 6, ["shell"] = 7, ["display"] = 8, ["subagent"] = 9 };
        return string.Join(",", out_.Distinct().OrderBy(t => rank.TryGetValue(t, out int r) ? r : 99));
    }

    private bool IsAdmin => (User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? User.FindFirst("IsAdmin")?.Value ?? "false").ToLower() == "true";

    public IActionResult Create()
    {
        if (!IsAdmin) return Forbid();
        return View(DefaultAgent());
    }

    [HttpPost]
    public async Task<IActionResult> Create(Agent form, string[] selectedTools)
    {
        if (!IsAdmin) return Forbid();
        Console.WriteLine($"[dbg] Name='{form.Name}' Model='{form.Model}' Bridge='{form.BridgeUrl}' Temp='{form.Temperature}' Valid={ModelState.IsValid}");
        foreach (var kv in ModelState) foreach (var e in kv.Value.Errors) Console.WriteLine($"[dbg] {kv.Key}: {e.ErrorMessage}");
        if (string.IsNullOrWhiteSpace(form.Name) || string.IsNullOrWhiteSpace(form.Model))
        {
            ViewBag.Error = "Nome e model obrigatórios.";
            return View(form);
        }
        form.Tools = ExpandTools(selectedTools);
        await agentsWrite.Add(form);
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Edit(int id)
    {
        if (!IsAdmin) return Forbid();
        var agent = await agents.Get(new Agent { Id = id });
        if (agent is null) return RedirectToAction("Index");
        ViewBag.List = await agents.Query(new Agent());
        return View(agent);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(Agent form, string[] selectedTools)
    {
        if (!IsAdmin) return Forbid();
        // Nunca confia no binding integral: recarrega e aplica só os campos
        // editáveis (preserva measured_tps e demais colunas).
        if (form.Id is null) return RedirectToAction("Index");
        var existing = await agents.Get(new Agent { Id = form.Id });
        if (existing is null) return RedirectToAction("Index");
        existing.Name = form.Name ?? "";
        existing.Model = form.Model ?? "";
        existing.BridgeUrl = form.BridgeUrl ?? "";
        existing.Temperature = form.Temperature;
        existing.Thinking = form.Thinking ?? "";
        existing.SystemPrompt = form.SystemPrompt ?? "";
        existing.Role = form.Role ?? "";
        existing.Mode = form.Mode ?? "";
        existing.Active = form.Active;
        existing.Tools = ExpandTools(selectedTools);
        await agentsWrite.Update(existing, new Agent { Id = existing.Id });
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin) return Forbid();
        await agentsWrite.Remove(new Agent { Id = id });
        return RedirectToAction("Index");
    }
}
