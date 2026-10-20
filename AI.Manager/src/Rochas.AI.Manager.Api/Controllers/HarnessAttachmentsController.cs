using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/harnessattachments")]
public sealed class HarnessAttachmentsController : ControllerBase
{
    private readonly IHarnessAttachmentService _svc = null!;
    public HarnessAttachmentsController(IHarnessAttachmentService svc) { _svc = svc; }
    [HttpGet("version/{versionId:int}")] public async Task<IActionResult> ListByVersion(int versionId, CancellationToken ct) => Ok(await _svc.ListByVersionAsync(versionId, ct));
    [HttpPost] public async Task<IActionResult> Create([FromBody] HarnessAttachmentsCreateRequest req, CancellationToken ct) { var id = await _svc.CreateAsync(req.VersionId, req.Kind, req.Role, req.Content, ct); return Created(string.Empty, new { id }); }
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await _svc.DeleteAsync(id, ct); return NoContent(); }
}
public sealed record HarnessAttachmentsCreateRequest(int VersionId, string Kind, string Role, string Content);
