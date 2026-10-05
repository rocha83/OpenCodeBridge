using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.DapperRepository;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// CRUD de usuarios via GenericRepository. Comentarios pt-BR.
[Authorize]
public sealed class UsersController(GenericRepository<User> users) : Controller
{
    public async Task<IActionResult> Index()
        => View(await users.Query(new User()));

    public IActionResult Create() => View(new User());

    [HttpPost]
    public async Task<IActionResult> Create(User model, string password)
    {
        if (string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(password))
        {
            ViewBag.Error = "Email e senha obrigatórios.";
            return View(model);
        }
        model.Email = model.Email.Trim().ToLowerInvariant();
        model.PasswordHash = PasswordHasher.Hash(password);
        await users.Add(model);
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Toggle(int id)
    {
        var user = await users.Get(new User { Id = id });
        if (user is not null)
        {
            user.Active = !user.Active;
            await users.Update(user, new User { Id = id });
        }
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Delete(int id)
    {
        await users.Remove(new User { Id = id });
        return RedirectToAction("Index");
    }
}
