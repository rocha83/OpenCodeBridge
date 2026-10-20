using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/harnesslinter")]
public sealed class HarnessLinterController : ControllerBase
{
    private readonly IHarnessLinter _svc = null!;
    public HarnessLinterController(IHarnessLinter svc) { _svc = svc; }
    [HttpPost] public IActionResult Lint([FromBody] HarnessLinterLintRequest req) { var findings = _svc.FindHardcodes(req.Content ?? string.Empty); return Ok(new { ok = findings.Count == 0, findings }); }
}
public sealed record HarnessLinterLintRequest(string? Content);
