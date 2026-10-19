using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TrainingJobsController(ITrainingJobService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTrainingJobRequest req, CancellationToken ct)
    {
        var id = await service.CreateAsync(req.ModelArtifactId, req.DatasetId, req.AdvancedMode, req.ConfigurationJson, ct);
        return Accepted(new { id });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var job = await service.GetAsync(id, ct);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.ListAsync(ct));

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await service.CancelAsync(id, ct);
        return NoContent();
    }
}

public sealed record CreateTrainingJobRequest(Guid? ModelArtifactId, Guid? DatasetId, bool AdvancedMode, string? ConfigurationJson);
