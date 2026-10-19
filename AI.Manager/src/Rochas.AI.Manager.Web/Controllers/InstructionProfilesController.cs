using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Profiles;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/instructionprofiles")]
public sealed class InstructionProfilesController(IInstructionProfileService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateInstructionProfileRequest req, CancellationToken ct)
    { var id = await svc.CreateAsync(req.Name, req.Description, req.InstructionsJson, req.Version, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPost("{id:int}/approve")] public async Task<IActionResult> Approve(int id, CancellationToken ct) { await svc.ApproveAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/deprecate")] public async Task<IActionResult> Deprecate(int id, CancellationToken ct) { await svc.DeprecateAsync(id, ct); return NoContent(); }
}
public sealed record CreateInstructionProfileRequest(string Name, string? Description, string? InstructionsJson, string? Version);
