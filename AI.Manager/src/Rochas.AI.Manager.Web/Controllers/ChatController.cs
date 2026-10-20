using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Entities;
using Rochas.Data.Specification.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

public sealed class ChatController(IGenericRepository<Agent> agentRepository) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var agents = await agentRepository.Query(new Agent());
        ViewBag.Agents = agents;
        return View();
    }
}
