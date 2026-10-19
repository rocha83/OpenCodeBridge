using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TrainingJobsApiController(ITrainingJobService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTrainingJobRequest req, CancellationToken ct)
    {
        var id = await service.CreateAsync(req.ModelArtifactId, req.DatasetId, req.AdvancedMode, req.ConfigurationJson, ct);
        return Accepted(new { id });
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var job = await service.GetAsync(id, ct);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.ListAsync(ct));

    [HttpPost("{id:int}/cancel")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        await service.CancelAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/start")]
    public async Task<IActionResult> Start(int id, CancellationToken ct) { await service.StartAsync(id, ct); return NoContent(); }

    [HttpPost("{id:int}/progress")]
    public async Task<IActionResult> Progress(int id, [FromBody] ProgressRequest req, CancellationToken ct)
    { await service.ReportProgressAsync(id, req.LastLoss, req.VramUsedMb, ct); return NoContent(); }

    [HttpPost("{id:int}/complete")]
    public async Task<IActionResult> Complete(int id, CancellationToken ct) { await service.CompleteAsync(id, ct); return NoContent(); }

    [HttpPost("{id:int}/fail")]
    public async Task<IActionResult> Fail(int id, [FromBody] FailRequest req, CancellationToken ct) { await service.FailAsync(id, req.ErrorMessage, ct); return NoContent(); }
}
public sealed record ProgressRequest(double? LastLoss, double? VramUsedMb);
public sealed record FailRequest(string ErrorMessage);

public sealed record CreateTrainingJobRequest(int? ModelArtifactId, int? DatasetId, bool AdvancedMode, string? ConfigurationJson);
