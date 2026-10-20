using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/harnessruns")]
public sealed class HarnessRunsController : ControllerBase
{
    private readonly IHarnessRunService _svc = null!;
    public HarnessRunsController(IHarnessRunService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpGet("version/{profileVersionId:int}")] public async Task<IActionResult> ListByVersion(int profileVersionId, CancellationToken ct) => Ok(await _svc.ListByProfileVersionAsync(profileVersionId, ct));
    [HttpPost] public async Task<IActionResult> Record([FromBody] HarnessRunsRecordRequest req, CancellationToken ct) { var id = await _svc.RecordAsync(req.EvidenceId, req.ProfileVersionId, req.ModelBindingId, req.RequestHash, req.ToolCalls, req.FilesCreated, req.ResultCode, req.DurationMs, req.TokensPerSecond, ct); return Created(string.Empty, new { id }); }
}
public sealed record HarnessRunsRecordRequest(int EvidenceId, int ProfileVersionId, int ModelBindingId, string? RequestHash, int ToolCalls, int FilesCreated, int ResultCode, long DurationMs, double TokensPerSecond);
