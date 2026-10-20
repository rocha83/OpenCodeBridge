using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Rochas.AI.Manager.Domain.Interfaces;

public sealed class ReportQuery
{
    public string Entity { get; set; } = "TrainingJob";
    public string? Status { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? AccuracyBand { get; set; }
    public double? AccuracyMin { get; set; }
    public double? AccuracyMax { get; set; }
    public string? ModelFamily { get; set; }
    public string? Mode { get; set; }
    public int? UserId { get; set; }
    public int? AgentId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public sealed class ReportResult
{
    public List<Dictionary<string, object?>> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public interface IReportService
{
    Task<ReportResult> QueryAsync(ReportQuery query, CancellationToken ct = default);
    Task<byte[]> ExportAsync(ReportQuery query, string format, CancellationToken ct = default);
}