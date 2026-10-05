using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.DapperRepository;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// CRUD de agentes/modelos via GenericRepository. Comentarios pt-BR.
[Authorize]
public sealed class AgentsController(GenericRepository<Agent> agents) : Controller
{
    public async Task<IActionResult> Index()
        => View(await agents.Query(new Agent()));

    public IActionResult Create() => View(new Agent());

    [HttpPost]
    public async Task<IActionResult> Create(Agent form)
    {
        Console.WriteLine($"[dbg] Name='{form.Name}' Model='{form.Model}' Bridge='{form.BridgeUrl}' Temp='{form.Temperature}' Valid={ModelState.IsValid}");
        foreach (var kv in ModelState) foreach (var e in kv.Value.Errors) Console.WriteLine($"[dbg] {kv.Key}: {e.ErrorMessage}");
        if (string.IsNullOrWhiteSpace(form.Name) || string.IsNullOrWhiteSpace(form.Model))
        {
            ViewBag.Error = "Nome e model obrigatórios.";
            return View(form);
        }
        await agents.Add(form);
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Edit(int id)
    {
        var agent = await agents.Get(new Agent { Id = id });
        return agent is null ? RedirectToAction("Index") : View(agent);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(Agent form)
    {
        await agents.Update(form, new Agent { Id = form.Id });
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Delete(int id)
    {
        await agents.Remove(new Agent { Id = id });
        return RedirectToAction("Index");
    }
}
