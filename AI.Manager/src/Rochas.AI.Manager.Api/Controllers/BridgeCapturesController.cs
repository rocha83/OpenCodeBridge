using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/bridgecaptures")]
public sealed class BridgeCapturesController : ControllerBase
{
    private readonly IBridgeCaptureService _svc = null!;
    public BridgeCapturesController(IBridgeCaptureService svc) { _svc = svc; }
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await _svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await _svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Record([FromBody] BridgeCapturesRecordRequest req, CancellationToken ct) { var id = await _svc.RecordAsync(req.Mode, req.RequestJson, req.ResponseJson, req.RequestHash, ct); return Created(string.Empty, new { id }); }
}
public sealed record BridgeCapturesRecordRequest(string Mode, string? RequestJson, string? ResponseJson, string? RequestHash);
