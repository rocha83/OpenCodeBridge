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
        // Bloqueio temporario apos falhas seguidas (anti força-bruta).
        if (user is not null && !string.IsNullOrEmpty(user.LockedUntil)
            && string.Compare(user.LockedUntil, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.Ordinal) > 0)
        {
            ViewBag.Error = $"Conta bloqueada até {user.LockedUntil} (tentativas excedidas).";
            return View();
        }
        // Senha obrigatória: sem ela não há como atrelar a sessão ao dono.
        if (user is null || string.IsNullOrEmpty(password) || !passwords.Verify(password, user.PasswordHash ?? ""))
        {
            if (user is not null) await RegisterFailure(user);
            ViewBag.Error = "Email ou senha inválidos.";
            return View();
        }
        user.FailedAttempts = 0;
        user.LockedUntil = "";
        await usersWrite.Update(user, new User { Id = user.Id });
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, (user.Id ?? 0).ToString()), new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Email, user.Email), new Claim("IsAdmin", user.IsAdmin.ToString()) };
        await HttpContext.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        return RedirectToAction("Index", "Chat");
    }

    // 5 falhas seguidas = 15 min de bloqueio (admin desbloqueia em Usuários).
    private async Task RegisterFailure(User user)
    {
        try
        {
            user.FailedAttempts++;
            if (user.FailedAttempts >= 5)
            {
                user.LockedUntil = DateTime.Now.AddMinutes(15).ToString("yyyy-MM-dd HH:mm:ss");
                user.FailedAttempts = 0;
            }
            await usersWrite.Update(user, new User { Id = user.Id });
        }
        catch { }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction("Login");
    }
}
