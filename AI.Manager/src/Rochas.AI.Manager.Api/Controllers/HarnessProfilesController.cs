using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/harnessprofiles")]
public sealed class HarnessProfilesController : ControllerBase
{
    private readonly IHarnessProfileService _svc = null!;
    public HarnessProfilesController(IHarnessProfileService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var p = await _svc.GetAsync(id, ct); return p is null ? NotFound() : Ok(p); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] HarnessProfilesCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Name, req.Description, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPost("{id:int}/versions")] public async Task<IActionResult> AddVersion(int id, [FromBody] HarnessProfilesAddVersionRequest req, CancellationToken ct) { var vid = await _svc.AddVersionAsync(id, req.Mode, req.Checksum, ct); return Created(string.Empty, new { id = vid }); }
    [HttpPost("versions/{versionId:int}/approve")] public async Task<IActionResult> Approve(int versionId, [FromBody] HarnessProfilesApproveRequest req, CancellationToken ct) { await _svc.ApproveVersionAsync(versionId, req.ApprovedBy, ct); return NoContent(); }
}
public sealed record HarnessProfilesCreateRequest(string Name, string? Description);
public sealed record HarnessProfilesAddVersionRequest(string Mode, string? Checksum);
public sealed record HarnessProfilesApproveRequest(string ApprovedBy);
