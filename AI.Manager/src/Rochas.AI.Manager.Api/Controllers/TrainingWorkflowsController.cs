using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public sealed class TrainingWorkflowsController(ITrainingWorkflowService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await service.ListAsync(ct));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var c = await service.GetAsync(id, ct);
        return c is null ? NotFound() : Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        var id = await service.CreateAsync(req.Title, req.Description, req.ExperimentId, req.TargetAccuracy, ct);
        return Ok(new { id });
    }

    [HttpPost("{id:int}/move")]
    public async Task<IActionResult> Move(int id, [FromBody] MoveRequest req, CancellationToken ct)
    {
        await service.MoveAsync(id, req.StatusId, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/accuracy")]
    public async Task<IActionResult> Accuracy(int id, [FromBody] AccuracyRequest req, CancellationToken ct)
    {
        await service.UpdateAccuracyAsync(id, req.Accuracy, ct);
        return NoContent();
    }

    [HttpPost("{id:int}/evaluate")]
    public async Task<IActionResult> Evaluate(int id, [FromBody] EvaluateRequest req, CancellationToken ct)
    {
        var result = await service.EvaluateAsync(id, req.Expected, req.Actual, ct);
        return Ok(new { score = result.Score, band = result.Band.ToString(), matched = result.MatchedTokens, expected = result.ExpectedTokens, actual = result.ActualTokens });
    }

    public sealed record CreateRequest(string Title, string? Description, int? ExperimentId, double TargetAccuracy);
    public sealed record MoveRequest(int StatusId);
    public sealed record AccuracyRequest(double Accuracy);
    public sealed record EvaluateRequest(string Expected, string Actual);
}
