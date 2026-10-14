using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// Login/logout com cookie (sem Identity). Comentarios pt-BR.
[AllowAnonymous]
public sealed class AccountController(IGenericRepository<User> users, IPersistenceRepository<User> usersWrite, Services.IPasswordHasher passwords) : Controller
{
    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string password)
    {
        string mail = (email ?? "").Trim().ToLowerInvariant();
        var found = await users.Query(new User { Email = mail });
        var user = found.FirstOrDefault(u => u.Active);
        // Senha obrigatória: sem ela não há como atrelar a sessão ao dono.
        if (user is null || string.IsNullOrEmpty(password) || !passwords.Verify(password, user.PasswordHash ?? ""))
        {
            ViewBag.Error = "Email ou senha inválidos.";
            return View();
        }
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, (user.Id ?? 0).ToString()), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Email, user.Email), new Claim("IsAdmin", user.IsAdmin.ToString()) };
        await HttpContext.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        return RedirectToAction("Index", "Chat");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction("Login");
    }
}
