using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class HarnessController(IHarnessProfileService service, IHarnessLinter linter) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var profiles = await service.ListAsync(ct);
        return View(profiles);
    }

    [HttpGet("Partial/List")]
    public async Task<IActionResult> ListPartial(CancellationToken ct)
    {
        var profiles = await service.ListAsync(ct);
        return PartialView("_ProfileList", profiles);
    }

    [HttpPost("Create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([FromForm] string name, [FromForm] string? description, CancellationToken ct)
    {
        var id = await service.CreateAsync(name, description, ct);
        return Json(new { id });
    }

    [HttpPost("Version")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddVersion([FromForm] int profileId, [FromForm] string mode, [FromForm] string? checksum, CancellationToken ct)
    {
        var vid = await service.AddVersionAsync(profileId, mode, checksum, ct);
        return Json(new { id = vid });
    }

    [HttpPost("Approve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve([FromForm] int versionId, [FromForm] string approvedBy, CancellationToken ct)
    {
        await service.ApproveVersionAsync(versionId, approvedBy, ct);
        return NoContent();
    }

    [HttpPost("Lint")]
    [ValidateAntiForgeryToken]
    public IActionResult Lint([FromBody] LintRequest req)
    {
        var findings = linter.FindHardcodes(req.Content ?? string.Empty);
        return Json(new { ok = findings.Count == 0, findings });
    }

    [HttpGet("Partial/Preview/{id:int}")]
    public async Task<IActionResult> Preview(int id, CancellationToken ct)
    {
        var profile = await service.GetAsync(id, ct);
        if (profile is null) return NotFound();
        return PartialView("_ProfilePreview", profile);
    }
}

public sealed record LintRequest(string? Content);
