using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Datasets;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/datasets")]
public sealed class DatasetsController : ControllerBase
{
    private readonly IDatasetService _svc = null!;
    public DatasetsController(IDatasetService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateDatasetRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Name, req.Format, req.Version, req.FilePath, req.FileHash, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPost("{id:int}/validate")] public async Task<IActionResult> Validate(int id, [FromBody] DatasetsValidateRequest req, CancellationToken ct) { await _svc.ValidateAsync(id, req.IsValid, ct); return NoContent(); }
    [HttpPost("{id:int}/validate-format")] public async Task<IActionResult> ValidateFormat(int id, [FromBody] DatasetsValidateFormatRequest req, CancellationToken ct) { var ok = await _svc.ValidateFormatAsync(id, req.Mode, ct); return Ok(new { valid = ok }); }
}
public sealed record CreateDatasetRequest(string Name, string Format, string? Version, string? FilePath, string? FileHash);
public sealed record DatasetsValidateRequest(bool IsValid);
public sealed record DatasetsValidateFormatRequest(string Mode);
