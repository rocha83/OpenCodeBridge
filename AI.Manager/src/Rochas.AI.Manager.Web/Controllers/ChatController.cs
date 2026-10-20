using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Entities;
using Rochas.Data.Specification.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class ChatController(IGenericRepository<Agent> agents) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var list = await agents.QueryRaw("SELECT * FROM agents", new Dictionary<string, object>());
        ViewBag.Agents = list;
        return View();
    }
}