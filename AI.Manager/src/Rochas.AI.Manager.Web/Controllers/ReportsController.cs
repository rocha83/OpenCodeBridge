using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class ReportsController : Controller
{
    private readonly IReportService _svc;
    public ReportsController(IReportService svc) { _svc = svc; }

    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet("Preview")]
    public async Task<IActionResult> Preview([FromQuery] ReportQuery q, CancellationToken ct)
    {
        q ??= new ReportQuery();
        var r = await _svc.QueryAsync(q, ct);
        return PartialView("_ReportPreview", r);
    }

    [HttpGet("Export")]
    public async Task<IActionResult> Export([FromQuery] ReportQuery q, string format = "csv", CancellationToken ct = default)
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