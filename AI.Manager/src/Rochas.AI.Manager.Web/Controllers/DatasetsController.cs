using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Datasets;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/datasets")]
public sealed class DatasetsController(IDatasetService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateDatasetRequest req, CancellationToken ct)
    { var id = await svc.CreateAsync(req.Name, req.Format, req.Version, req.FilePath, req.FileHash, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPost("{id:int}/validate")] public async Task<IActionResult> Validate(int id, [FromBody] ValidateRequest req, CancellationToken ct) { await svc.ValidateAsync(id, req.IsValid, ct); return NoContent(); }
}
public sealed record CreateDatasetRequest(string Name, string Format, string? Version, string? FilePath, string? FileHash);
public sealed record ValidateRequest(bool IsValid);
