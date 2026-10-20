using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/toolpolicies")]
public sealed class ToolPoliciesController : ControllerBase
{
    private readonly IToolPolicyService _svc = null!;
    public ToolPoliciesController(IToolPolicyService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] ToolPoliciesCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Role, req.Allow, req.Deny, req.RequireCall, req.MaxTurns, req.StallLimit, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] ToolPoliciesUpdateRequest req, CancellationToken ct) { await _svc.UpdateAsync(id, req.Allow, req.Deny, req.RequireCall, req.MaxTurns, req.StallLimit, ct); return NoContent(); }
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await _svc.DeleteAsync(id, ct); return NoContent(); }
}
public sealed record ToolPoliciesCreateRequest(string Role, string Allow, string Deny, bool RequireCall, int MaxTurns, int StallLimit);
public sealed record ToolPoliciesUpdateRequest(string Allow, string Deny, bool RequireCall, int MaxTurns, int StallLimit);
