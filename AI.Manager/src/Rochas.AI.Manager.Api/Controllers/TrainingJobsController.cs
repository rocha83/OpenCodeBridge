using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/trainingjobs")]
public sealed class TrainingJobsController : ControllerBase
{
    private readonly ITrainingJobService _svc = null!;
    public TrainingJobsController(ITrainingJobService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var job = await _svc.GetAsync(id, ct); return job is null ? NotFound() : Ok(job); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] TrainingJobsCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.ModelArtifactId, req.DatasetId, req.AdvancedMode, req.TrainingMethod ?? "SFT", req.ConfigurationJson, ct); return Accepted(new { id }); }
    [HttpPost("{id:int}/cancel")] public async Task<IActionResult> Cancel(int id, CancellationToken ct) { await _svc.CancelAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/start")] public async Task<IActionResult> Start(int id, CancellationToken ct) { await _svc.StartAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/progress")] public async Task<IActionResult> Progress(int id, [FromBody] TrainingJobsProgressRequest req, CancellationToken ct) { await _svc.ReportProgressAsync(id, req.LastLoss, req.VramUsedMb, ct); return NoContent(); }
    [HttpPost("{id:int}/complete")] public async Task<IActionResult> Complete(int id, CancellationToken ct) { await _svc.CompleteAsync(id, ct); return NoContent(); }
    [HttpPost("{id:int}/fail")] public async Task<IActionResult> Fail(int id, [FromBody] TrainingJobsFailRequest req, CancellationToken ct) { await _svc.FailAsync(id, req.ErrorMessage, ct); return NoContent(); }
    [HttpPost("{id:int}/run")] public IActionResult Run(int id) { return Accepted(new { id, status = "queued" }); }
}
public sealed record TrainingJobsCreateRequest(int? ModelArtifactId, int? DatasetId, bool AdvancedMode, string? TrainingMethod, string? ConfigurationJson);
public sealed record TrainingJobsProgressRequest(double? LastLoss, double? VramUsedMb);
public sealed record TrainingJobsFailRequest(string ErrorMessage);
