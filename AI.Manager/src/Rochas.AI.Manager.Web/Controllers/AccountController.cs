using Rochas.DapperRepository;
using Rochas.Data.Specification.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Entities;
using Rochas.AI.Manager.Domain.Interfaces;
using System.Security.Claims;

namespace Rochas.AI.Manager.Web.Controllers;

public sealed class AccountController(IGenericRepository<User> users, IPasswordHasher passwords) : Controller
{
    [HttpGet] public IActionResult Login() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string password, CancellationToken ct)
    {
        var list = await users.Query(new User { Email = email });
        var user = list.FirstOrDefault();
        if (user is null || !passwords.Verify(password, user.PasswordHash))
        {
            ViewBag.Error = "Credenciais inválidas.";
            return View();
        }
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.Email, user.Email),
            new("IsAdmin", user.IsAdmin.ToString().ToLower())
        };
        if (user.IsAdmin) claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return RedirectToAction("Index", "Harness");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Login");
    }
}
