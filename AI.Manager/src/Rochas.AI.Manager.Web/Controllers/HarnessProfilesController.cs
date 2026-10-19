using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/harnessprofiles")]
public sealed class HarnessProfilesApiController(IHarnessProfileService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.ListAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var p = await service.GetAsync(id, ct);
        return p is null ? NotFound() : Ok(p);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateHarnessProfileRequest req, CancellationToken ct)
    {
        var id = await service.CreateAsync(req.Name, req.Description, ct);
        return CreatedAtAction(nameof(Get), new { id }, new { id });
    }

    [HttpPost("{id:int}/versions")]
    public async Task<IActionResult> AddVersion(int id, [FromBody] AddVersionRequest req, CancellationToken ct)
    {
        var vid = await service.AddVersionAsync(id, req.Mode, req.Checksum, ct);
        return Created(string.Empty, new { id = vid });
    }

    [HttpPost("versions/{versionId:int}/approve")]
    public async Task<IActionResult> Approve(int versionId, [FromBody] ApproveRequest req, CancellationToken ct)
    {
        await service.ApproveVersionAsync(versionId, req.ApprovedBy, ct);
        return NoContent();
    }
}

public sealed record CreateHarnessProfileRequest(string Name, string? Description);
public sealed record AddVersionRequest(string Mode, string? Checksum);
public sealed record ApproveRequest(string ApprovedBy);
