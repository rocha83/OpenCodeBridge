using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Models;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class ModelArtifactsController(IModelArtifactService svc) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var list = await svc.ListAsync(ct);
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm] string name, [FromForm] string family, [FromForm] string format, [FromForm] bool isTrainable, CancellationToken ct)
    {
        await svc.CreateAsync(name, family, null, format, null, isTrainable, ct);
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, CancellationToken ct)
    {
        await svc.ApproveAsync(id, ct);
        return RedirectToAction("Index");
    }
}
