using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class ModelBindingsController(IModelBindingService svc) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await svc.ListAsync(ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string provider, string modelId, CancellationToken ct)
    {
        await svc.CreateAsync(provider, modelId, 4096, 32768, "off", 0.5, ct);
        return RedirectToAction("Index");
    }
}
