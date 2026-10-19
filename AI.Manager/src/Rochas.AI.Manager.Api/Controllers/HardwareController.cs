using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Route("api/hardware")]
public sealed class HardwareController : ControllerBase
{
    private readonly IHardwareService _svc = null!;
    public HardwareController(IHardwareService svc) { _svc = svc; }
    [HttpGet("detect")] public async Task<IActionResult> Detect(CancellationToken ct) => Ok(await _svc.DetectAsync(ct));
}
