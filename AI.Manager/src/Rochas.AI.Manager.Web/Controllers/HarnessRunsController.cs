using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/harnessruns")]
public sealed class HarnessRunsController(IHarnessRunService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpGet("version/{profileVersionId:int}")] public async Task<IActionResult> ListByVersion(int profileVersionId, CancellationToken ct) => Ok(await svc.ListByProfileVersionAsync(profileVersionId, ct));
    [HttpPost] public async Task<IActionResult> Record([FromBody] RecordHarnessRunRequest req, CancellationToken ct)
    { var id = await svc.RecordAsync(req.EvidenceId, req.ProfileVersionId, req.ModelBindingId, req.RequestHash, req.ToolCalls, req.FilesCreated, req.ResultCode, req.DurationMs, req.TokensPerSecond, ct); return Created(string.Empty, new { id }); }
}
public sealed record RecordHarnessRunRequest(int EvidenceId, int ProfileVersionId, int ModelBindingId, string? RequestHash, int ToolCalls, int FilesCreated, int ResultCode, long DurationMs, double TokensPerSecond);
