using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/toolpolicies")]
public sealed class ToolPoliciesController(IToolPolicyService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateToolPolicyRequest req, CancellationToken ct)
    { var id = await svc.CreateAsync(req.Role, req.Allow, req.Deny, req.RequireCall, req.MaxTurns, req.StallLimit, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPut("{id:int}")] public async Task<IActionResult> Update(int id, [FromBody] UpdateToolPolicyRequest req, CancellationToken ct)
    { await svc.UpdateAsync(id, req.Allow, req.Deny, req.RequireCall, req.MaxTurns, req.StallLimit, ct); return NoContent(); }
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteAsync(id, ct); return NoContent(); }
}
public sealed record CreateToolPolicyRequest(string Role, string Allow, string Deny, bool RequireCall, int MaxTurns, int StallLimit);
public sealed record UpdateToolPolicyRequest(string Allow, string Deny, bool RequireCall, int MaxTurns, int StallLimit);
