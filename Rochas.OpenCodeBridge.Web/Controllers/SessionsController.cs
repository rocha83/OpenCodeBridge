using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Controllers;

// Consulta de sessões de chat (mesmo padrão do CRUD de agentes). Comentários pt-BR.
[Authorize]
public sealed class SessionsController(
    IGenericRepository<Session> sessions,
    IGenericRepository<SessionMessage> messages,
    IGenericRepository<Agent> agents,
    IGenericRepository<User> users,
    ISessionService sessionService) : Controller
{
    private bool IsAdmin => (User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value ?? User.FindFirst("IsAdmin")?.Value ?? "false").ToLower() == "true";

    public async Task<IActionResult> Index(string? criterio = null, int userId = 0, string? de = null, string? ate = null, string[]? role = null)
    {
        var allUsers = (await users.Query(new User())).OrderBy(u => u.Name).ToList();
        var allAgents = await agents.Query(new Agent());
        ViewBag.Users = allUsers;
        ViewBag.AgentNames = allAgents.Where(a => a.Id.HasValue).ToDictionary(a => a.Id!.Value, a => a.Name ?? "");
        ViewBag.UserNames = allUsers.Where(u => u.Id.HasValue).ToDictionary(u => u.Id!.Value, u => u.Name ?? "");
        ViewBag.Criterio = criterio ?? "";
        ViewBag.SelUserId = userId;
        ViewBag.De = de ?? "";
        ViewBag.Ate = ate ?? "";
        ViewBag.SelRoles = role ?? [];
        ViewBag.Roles = new[] { "user", "assistant", "subagent" };

        // ORM: usuário exato + intervalo de criação (lados vazios = sem filtro).
        var f = new Session { UserId = userId };
        if (DateTime.TryParse(de, out DateTime d)) f.CreatedAt = d.Date;
        if (DateTime.TryParse(ate, out DateTime a)) f.CreatedAte = a.Date.AddDays(1).AddTicks(-1);
        var list = (await sessions.Query(f)).ToList();
        ViewBag.Total = list.Count;

        // Critério/papel: Search no conteúdo das mensagens ([Filterable]) e interseção.
        if (!string.IsNullOrWhiteSpace(criterio) || (role?.Length ?? 0) > 0)
        {
            ICollection<SessionMessage> msgs = string.IsNullOrWhiteSpace(criterio)
                ? await messages.Query(new SessionMessage())
                : await messages.Search(criterio.Trim());
            if ((role?.Length ?? 0) > 0)
                msgs = msgs.Where(m => role!.Contains(m.Role ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
            var ids = new HashSet<int>(msgs.Where(m => m.SessionId.HasValue).Select(m => m.SessionId!.Value));
            list = list.Where(s => s.Id.HasValue && ids.Contains(s.Id.Value)).ToList();
        }
        return View(list.OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.Id).ToList());
    }

    public async Task<IActionResult> Details(int id)
    {
        var s = await sessions.Get(new Session { Id = id });
        if (s is null) return RedirectToAction("Index");
        var allAgents = await agents.Query(new Agent());
        var allUsers = await users.Query(new User());
        ViewBag.Session = s;
        ViewBag.AgentName = allAgents.FirstOrDefault(a => a.Id == s.AgentId)?.Name ?? ("#" + s.AgentId);
        ViewBag.ExecName = s.ExecutorAgentId.HasValue
            ? allAgents.FirstOrDefault(a => a.Id == s.ExecutorAgentId.Value)?.Name ?? ("#" + s.ExecutorAgentId.Value)
            : "—";
        ViewBag.UserName = allUsers.FirstOrDefault(u => u.Id == s.UserId)?.Name ?? ("#" + s.UserId);
        ViewBag.IsAdmin = IsAdmin;
        var msgs = await sessionService.GetMessagesAsync(id, int.MaxValue);
        return View(msgs);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin) return Forbid();
        var s = await sessions.Get(new Session { Id = id });
        if (s is not null)
            await sessionService.DeleteAsync(id, s.UserId);
        return RedirectToAction("Index");
    }
}
