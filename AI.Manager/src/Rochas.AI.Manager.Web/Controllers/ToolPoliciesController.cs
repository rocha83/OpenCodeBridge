using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;

namespace Rochas.AI.Manager.Web.Controllers;

public sealed class ToolPoliciesController(IToolPolicyService svc) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await svc.ListAsync(ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string role, CancellationToken ct)
    {
        await svc.CreateAsync(role, "", "", false, 10, 3, ct);
        return RedirectToAction("Index");
    }
}
