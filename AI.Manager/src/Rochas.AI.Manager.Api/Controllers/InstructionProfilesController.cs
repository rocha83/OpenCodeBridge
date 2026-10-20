using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Profiles;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/instructionprofiles")]
public sealed class InstructionProfilesController : ControllerBase
{
    private readonly IInstructionProfileService _svc = null!;
    public InstructionProfilesController(IInstructionProfileService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] InstructionProfilesCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Name, req.Description, req.InstructionsJson, req.Version, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPost("{id:int}/approve")] public async Task<IActionResult> Approve(int id, CancellationToken ct) { await _svc.ApproveAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/deprecate")] public async Task<IActionResult> Deprecate(int id, CancellationToken ct) { await _svc.DeprecateAsync(id, ct); return NoContent(); }
}
public sealed record InstructionProfilesCreateRequest(string Name, string? Description, string? InstructionsJson, string? Version);
