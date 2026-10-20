using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportService _svc;
    public ReportsController(IReportService svc) { _svc = svc; }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] ReportQuery q, CancellationToken ct)
    {
        var r = await _svc.QueryAsync(q, ct);
        return Ok(r);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] ReportQuery q, [FromQuery] string format = "csv", CancellationToken ct = default)
    {
        var bytes = await _svc.ExportAsync(q, format, ct);
        var (mime, ext) = format.ToLowerInvariant() switch
        {
            "excel" or "xlsx" => ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
            "csv" => ("text/csv", "csv"),
            "pdf" => ("text/html", "html"),
            _ => ("application/octet-stream", "bin")
        };
        return File(bytes, mime, $"{q.Entity}.{ext}");
    }
}