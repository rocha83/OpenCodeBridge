using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Profiles;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/trainingpatterns")]
public sealed class TrainingPatternsController : ControllerBase
{
    private readonly ITrainingPatternService _svc = null!;
    public TrainingPatternsController(ITrainingPatternService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Create([FromBody] TrainingPatternsCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.Name, req.ProfileId, req.ModelArtifactId, req.PatternJson, ct); return CreatedAtAction(nameof(Get), new { id }, new { id }); }
}
public sealed record TrainingPatternsCreateRequest(string Name, int? ProfileId, int? ModelArtifactId, string? PatternJson);
