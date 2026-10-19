using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Experiments;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/experiments")]
public sealed class ExperimentsController(IExperimentService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateExperimentRequest req, CancellationToken ct)
    { var id = await svc.CreateAsync(req.Name, req.Hypothesis, req.BaseArtifactId, req.TunedArtifactId, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
    [HttpPost("{id:int}/start")] public async Task<IActionResult> Start(int id, CancellationToken ct) { await svc.StartAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/complete")] public async Task<IActionResult> Complete(int id, [FromBody] CompleteRequest req, CancellationToken ct) { await svc.CompleteAsync(id, req.MetricsJson, ct); return NoContent(); }
    [HttpPost("{id:int}/approve")] public async Task<IActionResult> Approve(int id, CancellationToken ct) { await svc.ApproveAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/reject")] public async Task<IActionResult> Reject(int id, CancellationToken ct) { await svc.RejectAsync(id, ct); return NoContent(); }
}
public sealed record CreateExperimentRequest(string Name, string? Hypothesis, int? BaseArtifactId, int? TunedArtifactId);
public sealed record CompleteRequest(string? MetricsJson);
