using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private static readonly HashSet<string> AllowedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "csv", "excel", "xlsx", "pdf", "html"
    };

    private readonly IReportService _svc;
    public ReportsController(IReportService svc) { _svc = svc; }

    [HttpGet("catalog")]
    public IActionResult Catalog() => Ok(_svc.GetCatalog());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id, [FromQuery] ReportQuery q, CancellationToken ct)
    {
        q ??= new ReportQuery();
        q.Entity = id;
        try
        {
            var result = await _svc.QueryAsync(q, ct);
            return Ok(result);
        }
        catch (ReportQueryException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] ReportQuery q, CancellationToken ct)
    {
        try
        {
            var result = await _svc.QueryAsync(q, ct);
            return Ok(result);
        }
        catch (ReportQueryException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] ReportQuery q, [FromQuery] string format = "csv", CancellationToken ct = default)
    {
        if (!AllowedFormats.Contains(format))
            return BadRequest(new { error = $"Formato invalido: '{format}'. Use: csv, excel, xlsx, pdf, html." });

        try
        {
            var bytes = await _svc.ExportAsync(q, format, ct);
            var (mime, ext) = MimeFor(format);
            return File(bytes, mime, $"{q.Entity}.{ext}");
        }
        catch (ReportQueryException ex)
        {
            return BadRequest(new { error = ex.Message });
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
