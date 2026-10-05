using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.DapperRepository;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// Login/logout com cookie (sem Identity). Comentarios pt-BR.
[AllowAnonymous]
public sealed class AccountController(GenericRepository<User> users) : Controller
{
    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    public async Task<IActionResult> Login(string email, string password)
    {
        var found = await users.Query(new User { Email = (email ?? "").Trim().ToLowerInvariant() });
        var user = found.FirstOrDefault(u => u.Active);
        if (user is null || !PasswordHasher.Verify(password ?? "", user.PasswordHash))
        {
            ViewBag.Error = "Credenciais inválidas.";
            return View();
        }
        var claims = new[] { new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Email, user.Email) };
        await HttpContext.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
        return RedirectToAction("Index", "Chat");
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return RedirectToAction("Login");
    }
}
