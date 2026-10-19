using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;

namespace Rochas.AI.Manager.Web.Controllers;

public sealed class BridgeCapturesController(IBridgeCaptureService svc) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await svc.ListAsync(ct));
}
