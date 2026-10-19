using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Application.Abstractions;

namespace Rochas.AI.Manager.Web.Controllers;

[ApiController]
[Route("api/hardware")]
public sealed class HardwareController(IHardwareService svc) : ControllerBase
{
    [HttpGet("detect")] public async Task<IActionResult> Detect(CancellationToken ct) => Ok(await svc.DetectAsync(ct));
}
