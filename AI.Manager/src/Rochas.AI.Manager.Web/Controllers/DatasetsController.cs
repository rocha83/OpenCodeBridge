using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;

namespace Rochas.AI.Manager.Web.Controllers;

public sealed class DatasetsController(IDatasetService svc) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await svc.ListAsync(ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string format, CancellationToken ct)
    {
        await svc.CreateAsync(name, format, null, null, null, ct);
        return RedirectToAction("Index");
    }
}
