using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class HarnessRunsController(IHarnessRunService svc) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await svc.ListAsync(ct));
}
