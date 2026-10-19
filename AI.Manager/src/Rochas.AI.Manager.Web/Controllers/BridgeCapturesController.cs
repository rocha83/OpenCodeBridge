using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/bridgecaptures")]
public sealed class BridgeCapturesController(IBridgeCaptureService svc) : ControllerBase
{
    [HttpGet] public async Task<IActionResult> List(CancellationToken ct) => Ok(await svc.ListAsync(ct));
    [HttpGet("{id:int}")] public async Task<IActionResult> Get(int id, CancellationToken ct) { var x = await svc.GetAsync(id, ct); return x is null ? NotFound() : Ok(x); }
    [HttpPost] public async Task<IActionResult> Record([FromBody] RecordBridgeCaptureRequest req, CancellationToken ct)
    { var id = await svc.RecordAsync(req.Mode, req.RequestJson, req.ResponseJson, req.RequestHash, ct); return Created(string.Empty, new { id }); }
}
public sealed record RecordBridgeCaptureRequest(string Mode, string? RequestJson, string? ResponseJson, string? RequestHash);
