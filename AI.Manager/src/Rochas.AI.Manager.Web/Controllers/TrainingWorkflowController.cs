using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Entities;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class TrainingWorkflowController(ITrainingWorkflowService service) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var cards = await service.ListAsync(ct);
        return View(cards);
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm] string title, [FromForm] string? description, [FromForm] int? experimentId, [FromForm] double targetAccuracy, CancellationToken ct)
    {
        var id = await service.CreateAsync(title, description, experimentId, targetAccuracy, ct);
        return Json(new { id });
    }

    [HttpPost("Move")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Move([FromForm] int id, [FromForm] int statusId, CancellationToken ct)
    {
        await service.MoveAsync(id, statusId, ct);
        return NoContent();
    }

    [HttpPost("Accuracy")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAccuracy([FromForm] int id, [FromForm] double accuracy, CancellationToken ct)
    {
        await service.UpdateAccuracyAsync(id, accuracy, ct);
        return NoContent();
    }

    [HttpPost("Reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject([FromForm] int id, [FromForm] string reason, CancellationToken ct)
    {
        await service.RejectAsync(id, reason, ct);
        return NoContent();
    }

    [HttpPost("Evaluate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate([FromForm] int id, [FromForm] string expected, [FromForm] string actual, CancellationToken ct)
    {
        var result = await service.EvaluateAsync(id, expected, actual, ct);
        return Json(new { score = result.Score, band = result.Band.ToString(), matched = result.MatchedTokens, expected = result.ExpectedTokens, actual = result.ActualTokens });
    }
}
