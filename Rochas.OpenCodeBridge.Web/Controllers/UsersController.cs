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

    public async Task<IActionResult> Index(string? criterio = null)
    {
        if (!IsAdmin) return Forbid();
        ViewBag.Criterio = criterio ?? "";
        // Filtro em memoria (OR nome/email; Search faria AND entre colunas).
        var all = await users.Query(new User());
        IEnumerable<User> list = all;
        if (!string.IsNullOrWhiteSpace(criterio))
        {
            string q = criterio.Trim();
            list = all.Where(u => (u.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (u.Email ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        return View(list.OrderBy(u => u.Name).ToList());
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

    public async Task<IActionResult> Edit(int id)
    {
        if (!IsAdmin) return Forbid();
        var user = await users.Get(new User { Id = id });
        if (user is null) return RedirectToAction("Index");
        return View(user);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, User form, string? password)
    {
        if (!IsAdmin) return Forbid();
        var user = await users.Get(new User { Id = id });
        if (user is null) return RedirectToAction("Index");
        if (string.IsNullOrWhiteSpace(form.Email))
        {
            ViewBag.Error = "Email obrigatório.";
            return View(user);
        }
        user.Name = (form.Name ?? "").Trim();
        user.Email = form.Email.Trim().ToLowerInvariant();
        user.Active = form.Active;
        user.IsAdmin = form.IsAdmin;
        if (!string.IsNullOrWhiteSpace(password)) user.PasswordHash = passwords.Hash(password);
        await usersWrite.Update(user, new User { Id = id });
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlock(int id)
    {
        if (!IsAdmin) return Forbid();
        var user = await users.Get(new User { Id = id });
        if (user is not null)
        {
            user.FailedAttempts = 0;
            user.LockedUntil = "";
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
