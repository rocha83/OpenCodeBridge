using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Services;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/permission")]
public sealed class PermissionController(PermissionService svc) : ControllerBase
{
    [HttpPost("check")]
    public IActionResult Check([FromBody] PermissionCheckRequest req)
    {
        var result = svc.PermissionOf(req.ModelId, req.Tool ?? "", req.Arguments);
        return Ok(new { action = result.Action, reason = result.Reason, role = result.Role, tool = result.Tool, notice = result.Notice });
    }

    public sealed record PermissionCheckRequest(string? ModelId, string? Tool, string? Arguments);
}
