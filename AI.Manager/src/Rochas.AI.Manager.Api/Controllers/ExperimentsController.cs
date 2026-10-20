using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Experiments;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/experiments")]
public sealed class ExperimentsController : ControllerBase
{
    private readonly IExperimentService _svc = null!;
    public ExperimentsController(IExperimentService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] ExperimentsCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Name, req.Hypothesis, req.BaseArtifactId, req.TunedArtifactId, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPost("{id:int}/start")] public async Task<IActionResult> Start(int id, CancellationToken ct) { await _svc.StartAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/complete")] public async Task<IActionResult> Complete(int id, [FromBody] ExperimentsCompleteRequest req, CancellationToken ct) { await _svc.CompleteAsync(id, req.MetricsJson, ct); return NoContent(); }
    [HttpPost("{id:int}/approve")] public async Task<IActionResult> Approve(int id, CancellationToken ct) { await _svc.ApproveAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/reject")] public async Task<IActionResult> Reject(int id, CancellationToken ct) { await _svc.RejectAsync(id, ct); return NoContent(); }
}
public sealed record ExperimentsCreateRequest(string Name, string? Hypothesis, int? BaseArtifactId, int? TunedArtifactId);
public sealed record ExperimentsCompleteRequest(string? MetricsJson);
