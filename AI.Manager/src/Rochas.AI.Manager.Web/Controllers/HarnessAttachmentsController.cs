using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/harnessattachments")]
public sealed class HarnessAttachmentsController(IHarnessAttachmentService svc) : ControllerBase
{
    [HttpGet("version/{versionId:int}")] public async Task<IActionResult> ListByVersion(int versionId, CancellationToken ct) => Ok(await svc.ListByVersionAsync(versionId, ct));
    [HttpPost] public async Task<IActionResult> Create([FromBody] CreateHarnessAttachmentRequest req, CancellationToken ct)
    { var id = await svc.CreateAsync(req.VersionId, req.Kind, req.Role, req.Content, ct); return Created(string.Empty, new { id }); }
    [HttpDelete("{id:int}")] public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteAsync(id, ct); return NoContent(); }
}
public sealed record CreateHarnessAttachmentRequest(int VersionId, string Kind, string Role, string Content);
