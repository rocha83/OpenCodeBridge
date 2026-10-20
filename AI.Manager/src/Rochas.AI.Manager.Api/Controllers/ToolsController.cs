using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Tools;
using System.Text.Json;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/tools")]
public sealed class ToolsController(BridgeToolExecutor executor) : ControllerBase
{
    [HttpPost("execute")]
    public IActionResult Execute([FromBody] ToolExecuteRequest req)
    {
        var result = executor.Execute(req.Name ?? "", req.Arguments ?? "", req.TimeoutSeconds ?? 300);
        return Ok(executor.ToolResultJson(result));
    }

    public sealed record ToolExecuteRequest(string? Name, string? Arguments, int? TimeoutSeconds);
}
