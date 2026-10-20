using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Web.Controllers;

[Authorize]
public sealed class ReportsController : Controller
{
    private static readonly HashSet<string> AllowedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "csv", "excel", "xlsx", "pdf", "html"
    };

    private readonly IReportService _svc;
    public ReportsController(IReportService svc) { _svc = svc; }

    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet("Preview")]
    public async Task<IActionResult> Preview([FromQuery] ReportQuery q, CancellationToken ct)
    {
        q ??= new ReportQuery();
        try
        {
            var result = await _svc.QueryAsync(q, ct);
            return PartialView("_ReportPreview", result);
        }
        catch (ReportQueryException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("Export")]
    public async Task<IActionResult> Export([FromQuery] ReportQuery q, string format = "csv", CancellationToken ct = default)
    {
        if (!AllowedFormats.Contains(format))
            return BadRequest($"Formato invalido: '{format}'. Use: csv, excel, xlsx, pdf, html.");

        try
        {
            var bytes = await _svc.ExportAsync(q, format, ct);
            var (mime, ext) = MimeFor(format);
            return File(bytes, mime, $"{q.Entity}.{ext}");
        }
        catch (ReportQueryException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    private static (string Mime, string Extension) MimeFor(string format) => format.ToLowerInvariant() switch
    {
        "excel" or "xlsx" => ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
        "csv" => ("text/csv", "csv"),
        "pdf" => ("application/pdf", "pdf"),
        "html" => ("text/html", "html"),
        _ => ("application/octet-stream", "bin")
    };
}
