using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.ExcelToJson;
using Rochas.PDFGenerator;
using Rochas.PDFGenerator.Helpers;
using System.Data;

namespace Rochas.AI.Manager.Infrastructure.Services;

public sealed class ReportService(IServiceProvider sp) : IReportService
{
    private static readonly Dictionary<string, string> TableMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TrainingJob"] = "training_jobs",
        ["Experiment"] = "experiments",
        ["HarnessRun"] = "harness_runs",
        ["TrainingWorkflowCard"] = "training_workflow_cards",
        ["ModelArtifact"] = "model_artifacts",
        ["Dataset"] = "datasets",
        ["InstructionProfile"] = "instruction_profiles",
        ["HarnessProfile"] = "harness_profiles",
        ["ToolPolicy"] = "tool_policies",
        ["ModelBinding"] = "model_bindings",
        ["BridgeCapture"] = "bridge_captures",
        ["User"] = "users",
        ["Agent"] = "agents"
    };

    private static readonly Dictionary<string, string> StatusColumn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TrainingJob"] = "status",
        ["Experiment"] = "status",
        ["TrainingWorkflowCard"] = "status_id",
        ["ModelArtifact"] = "status",
        ["InstructionProfile"] = "status",
        ["HarnessProfile"] = "status"
    };

    private static readonly Dictionary<string, Type> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TrainingJob"] = typeof(Rochas.AI.Manager.Domain.Training.TrainingJob),
        ["Experiment"] = typeof(Rochas.AI.Manager.Domain.Experiments.Experiment),
        ["HarnessRun"] = typeof(Rochas.AI.Manager.Domain.Harness.HarnessRun),
        ["TrainingWorkflowCard"] = typeof(Rochas.AI.Manager.Domain.Entities.TrainingWorkflowCard),
        ["ModelArtifact"] = typeof(Rochas.AI.Manager.Domain.Models.ModelArtifact),
        ["Dataset"] = typeof(Rochas.AI.Manager.Domain.Datasets.Dataset),
        ["InstructionProfile"] = typeof(Rochas.AI.Manager.Domain.Profiles.InstructionProfile),
        ["HarnessProfile"] = typeof(Rochas.AI.Manager.Domain.Harness.HarnessProfile),
        ["ToolPolicy"] = typeof(Rochas.AI.Manager.Domain.Harness.ToolPolicy),
        ["ModelBinding"] = typeof(Rochas.AI.Manager.Domain.Harness.ModelBinding),
        ["BridgeCapture"] = typeof(Rochas.AI.Manager.Domain.Harness.BridgeCapture),
        ["User"] = typeof(Rochas.AI.Manager.Domain.Entities.User),
        ["Agent"] = typeof(Rochas.AI.Manager.Domain.Entities.Agent)
    };

    public async Task<ReportResult> QueryAsync(ReportQuery q, CancellationToken ct = default)
    {
        if (!TypeMap.TryGetValue(q.Entity, out var type)) type = typeof(Rochas.AI.Manager.Domain.Training.TrainingJob);
        var repoType = typeof(Rochas.Data.Specification.Interfaces.IGenericRepository<>).MakeGenericType(type);
        var repo = sp.GetRequiredService(repoType);
        var queryMethod = repoType.GetMethod("Query", new[] { type, typeof(bool), typeof(bool) })!;
        var qb = queryMethod.Invoke(repo, new[] { Activator.CreateInstance(type), false, false })!;
        var awaiter = qb.GetType().GetMethod("GetAwaiter")!.Invoke(qb, null)!;
        var getResult = awaiter.GetType().GetMethod("GetResult")!.Invoke(awaiter, null)!;
        var list = (System.Collections.IEnumerable)getResult;
        var items = new List<Dictionary<string, object?>>();
        foreach (var obj in list)
        {
            var row = new Dictionary<string, object?>();
            foreach (var prop in type.GetProperties())
            {
                var colAttr = prop.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>();
                var name = colAttr?.Name ?? prop.Name;
                row[name] = prop.GetValue(obj);
            }
            // filters
            if (!string.IsNullOrWhiteSpace(q.Status))
            {
                var statusProp = type.GetProperty("Status") ?? type.GetProperty("StatusId");
                if (statusProp != null)
                {
                    var val = statusProp.GetValue(obj)?.ToString();
                    if (!string.Equals(val, q.Status, StringComparison.OrdinalIgnoreCase)) continue;
                }
            }
            items.Add(row);
        }
        // pagination
        var total = items.Count;
        var pageItems = items.Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToList();
        return new ReportResult { Items = pageItems, Total = total, Page = q.Page, PageSize = q.PageSize };
    }

    public async Task<byte[]> ExportAsync(ReportQuery q, string format, CancellationToken ct = default)
    {
        var result = await QueryAsync(q, ct);
        var json = JsonSerializer.Serialize(result.Items);
        using var parser = new ExcelToJsonParser();
        format = (format ?? "csv").ToLowerInvariant();
        if (format == "excel" || format == "xlsx")
            return parser.JsonToExcel(json, q.Entity);
        if (format == "csv")
            return parser.JsonToCsv(json, delimiter: ";");
        if (format == "pdf")
        {
            var dt = new DataTable(q.Entity);
            if (result.Items.Count > 0)
            {
                foreach (var columnKey in result.Items[0].Keys) dt.Columns.Add(columnKey);
                foreach (var row in result.Items)
                {
                    var dr = dt.NewRow();
                    foreach (var kv in row) dr[kv.Key] = kv.Value ?? DBNull.Value;
                    dt.Rows.Add(dr);
                }
            }
            var composer = new PDFComposer();
            var pdfBytes = composer.GeneratePdf(dt, new PdfConfig(), new PdfPlaceHolderStyle());
            return pdfBytes;
        }
        // html fallback
        var sb = new StringBuilder();
        sb.Append("<html><head><meta charset='utf-8'><title>Relatorio</title></head><body>");
        sb.Append($"<h1>Relatorio - {q.Entity}</h1>");
        sb.Append($"<p>Total: {result.Total} | Pagina {result.Page}</p>");
        sb.Append("<table border='1' cellpadding='4' cellspacing='0'><tr>");
        if (result.Items.Count > 0)
        {
            foreach (var columnKey in result.Items[0].Keys) sb.Append($"<th>{columnKey}</th>");
            sb.Append("</tr>");
            foreach (var row in result.Items)
            {
                sb.Append("<tr>");
                foreach (var cellValue in row.Values) sb.Append($"<td>{cellValue}</td>");
                sb.Append("</tr>");
            }
        }
        sb.Append("</table></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static bool HasColumn(string table, string col)
    {
        // whitelist known
        return table switch
        {
            "training_jobs" => false,
            "experiments" => false,
            "harness_runs" => false,
            "training_workflow_cards" => false,
            "users" => true,
            "agents" => true,
            _ => false
        };
    }
}