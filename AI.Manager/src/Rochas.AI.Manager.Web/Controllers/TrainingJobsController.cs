using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class TrainingJobsController(ITrainingJobService svc) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var list = await svc.ListAsync(ct);
        return View(list);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm] int? modelArtifactId, [FromForm] int? datasetId, [FromForm] bool advancedMode, [FromForm] string? trainingMethod, CancellationToken ct)
    {
        await svc.CreateAsync(modelArtifactId, datasetId, advancedMode, trainingMethod ?? "SFT", null, ct);
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(int id, CancellationToken ct)
    {
        await svc.StartAsync(id, ct);
        return RedirectToAction("Index");
    }
}
