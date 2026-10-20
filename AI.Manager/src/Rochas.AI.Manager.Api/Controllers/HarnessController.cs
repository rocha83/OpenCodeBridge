using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/harness")]
public sealed class HarnessController : ControllerBase
{
    private readonly IHarnessProfileService _svc;
    private readonly IHarnessAttachmentService _att;
    public HarnessController(IHarnessProfileService svc, IHarnessAttachmentService att) { _svc = svc; _att = att; }

    [HttpGet("{id:int}/content")]
    public async Task<IActionResult> Content(int id, CancellationToken ct)
    {
        var profile = await _svc.GetAsync(id, ct);
        if (profile is null) return NotFound();
        var ver = profile.Versions.OrderByDescending(v => v.Version).FirstOrDefault();
        if (ver is null) return NotFound();
        var atts = await _att.ListByVersionAsync(ver.Id, ct);
        var sb = new System.Text.StringBuilder();
        foreach (var attachment in atts.OrderBy(attachment => attachment.Kind))
        {
            sb.AppendLine($"===== {attachment.Kind} ({attachment.Role}) =====");
            sb.AppendLine(attachment.Content);
        }
        return Ok(new { profileId = id, versionId = ver.Id, version = ver.Version, content = sb.ToString(), charCount = sb.Length });
    }
}