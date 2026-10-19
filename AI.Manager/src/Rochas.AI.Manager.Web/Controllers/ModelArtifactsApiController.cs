using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Models;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/modelartifacts")]
public sealed class ModelArtifactsApiController(IModelArtifactService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateModelArtifactRequest req, CancellationToken ct)
    { var id = await svc.CreateAsync(req.Name, req.Family, req.Variant, req.Format, req.FilePath, req.IsTrainable, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPost("{id:int}/approve")] public async Task<IActionResult> Approve(int id, CancellationToken ct) { await svc.ApproveAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/deprecate")] public async Task<IActionResult> Deprecate(int id, CancellationToken ct) { await svc.DeprecateAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/hash")] public async Task<IActionResult> Hash(int id, [FromBody] HashRequest req, CancellationToken ct) { await svc.UpdateHashAsync(id, req.Hash, ct); return NoContent(); }
}
public sealed record CreateModelArtifactRequest(string Name, string Family, string? Variant, string Format, string? FilePath, bool IsTrainable);
public sealed record HashRequest(string Hash);
