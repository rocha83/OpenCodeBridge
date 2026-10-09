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
        model.PasswordHash = passwords.Hash(password);
        await usersWrite.Add(model);
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Toggle(int id)
    {
        var user = await users.Get(new User { Id = id });
        if (user is not null)
        {
            user.Active = !user.Active;
            await usersWrite.Update(user, new User { Id = id });
        }
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Delete(int id)
    {
        await usersWrite.Remove(new User { Id = id });
        return RedirectToAction("Index");
    }
}
