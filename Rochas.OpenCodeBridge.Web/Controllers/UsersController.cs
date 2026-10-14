using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// CRUD de usuarios via GenericRepository. Comentarios pt-BR.
[Authorize]
public sealed class UsersController(IGenericRepository<User> users, IPersistenceRepository<User> usersWrite, IPasswordHasher passwords) : Controller
{
    // Usuários: só admin (lista e mutações). Não-admin nem vê o link.
    private bool IsAdmin => (User.FindFirst("IsAdmin")?.Value ?? "false").ToLower() == "true";

    public async Task<IActionResult> Index()
    {
        if (!IsAdmin) return Forbid();
        return View(await users.Query(new User()));
    }

    public IActionResult Create()
    {
        if (!IsAdmin) return Forbid();
        return View(new User());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(User model, string password)
    {
        if (!IsAdmin) return Forbid();
        if (string.IsNullOrWhiteSpace(model.Email) || string.IsNullOrWhiteSpace(password))
        {
            ViewBag.Error = "Email e senha obrigatórios.";
            return View(model);
        }
        model.Email = model.Email.Trim().ToLowerInvariant();
        model.PasswordHash = passwords.Hash(password);
        await usersWrite.Add(model);
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        if (!IsAdmin) return Forbid();
        var user = await users.Get(new User { Id = id });
        if (user is not null)
        {
            user.Active = !user.Active;
            await usersWrite.Update(user, new User { Id = id });
        }
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin) return Forbid();
        await usersWrite.Remove(new User { Id = id });
        return RedirectToAction("Index");
    }
}
