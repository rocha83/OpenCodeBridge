using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Profiles;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/trainingpatterns")]
public sealed class TrainingPatternsController(ITrainingPatternService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateTrainingPatternRequest req, CancellationToken ct)
    { var id = await svc.CreateAsync(req.Name, req.ProfileId, req.ModelArtifactId, req.PatternJson, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
}
public sealed record CreateTrainingPatternRequest(string Name, int? ProfileId, int? ModelArtifactId, string? PatternJson);
