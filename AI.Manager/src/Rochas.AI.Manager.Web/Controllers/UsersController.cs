using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class UsersController(IUserService svc) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct) => View(await svc.ListAsync(ct));

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string email, string password, bool isAdmin, CancellationToken ct)
    {
        await svc.CreateAsync(name, email, password, isAdmin, ct);
        return RedirectToAction("Index");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await svc.DeleteAsync(id, ct);
        return RedirectToAction("Index");
    }
}
