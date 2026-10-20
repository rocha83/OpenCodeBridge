using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;
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

public sealed class ReportService : IReportService
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

    public async Task<ReportResult> QueryAsync(ReportQuery q, CancellationToken ct = default)
    {
        if (!TableMap.TryGetValue(q.Entity, out var table)) table = "training_jobs";
        var where = new List<string>();
        var pars = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            where.Add($"{(StatusColumn.TryGetValue(q.Entity, out var sc) ? sc : "status")} = @status");
            pars["@status"] = q.Status;
        }
        if (q.DateFrom.HasValue) { where.Add("created_at >= @df"); pars["@df"] = q.DateFrom.Value.ToString("o"); }
        if (q.DateTo.HasValue) { where.Add("created_at <= @dt"); pars["@dt"] = q.DateTo.Value.ToString("o"); }
        if (q.AccuracyMin.HasValue && table == "training_workflow_cards") { where.Add("accuracy >= @amin"); pars["@amin"] = q.AccuracyMin.Value; }
        if (q.AccuracyMax.HasValue && table == "training_workflow_cards") { where.Add("accuracy <= @amax"); pars["@amax"] = q.AccuracyMax.Value; }
        if (!string.IsNullOrWhiteSpace(q.Mode) && table == "harness_runs") { /* no mode column; skip */ }
        if (q.UserId.HasValue && HasColumn(table, "user_id")) { where.Add("user_id = @uid"); pars["@uid"] = q.UserId.Value; }
        if (q.AgentId.HasValue && HasColumn(table, "agent_id")) { where.Add("agent_id = @aid"); pars["@aid"] = q.AgentId.Value; }

        // accuracy band filter for training_workflow_cards
        if (!string.IsNullOrWhiteSpace(q.AccuracyBand) && table == "training_workflow_cards")
        {
            switch (q.AccuracyBand.ToLowerInvariant())
            {
                case "low": where.Add("(accuracy IS NULL OR accuracy < 0.5)"); break;
                case "medium": where.Add("accuracy >= 0.5 AND accuracy < 0.8"); break;
                case "high": where.Add("accuracy >= 0.8"); break;
            }
        }
        if (!string.IsNullOrWhiteSpace(q.ModelFamily) && table == "model_artifacts")
        {
            where.Add("family LIKE @mf"); pars["@mf"] = "%" + q.ModelFamily + "%";
        }

        var whereSql = where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "";
        var sql = $"SELECT * FROM {table}{whereSql} ORDER BY id DESC LIMIT @ps OFFSET @off";
        var countSql = $"SELECT COUNT(*) FROM {table}{whereSql}";

        using var conn = new SqliteConnection(AppDb.ConnectionString);
        await conn.OpenAsync(ct);
        var total = (long)(await new SqliteCommand(countSql, conn) { Parameters = { } }.ExecuteScalarAsync(ct) ?? 0L);
        // parameters for count
        // re-execute with params
        using var cmdCount = new SqliteCommand(countSql, conn);
        foreach (var kv in pars) cmdCount.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
        total = (long)(await cmdCount.ExecuteScalarAsync(ct) ?? 0L);

        using var cmd = new SqliteCommand(sql, conn);
        foreach (var kv in pars) cmd.Parameters.AddWithValue(kv.Key, kv.Value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ps", q.PageSize);
        cmd.Parameters.AddWithValue("@off", (q.Page - 1) * q.PageSize);

        var items = new List<Dictionary<string, object?>>();
        using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < rdr.FieldCount; i++)
                row[rdr.GetName(i)] = rdr.IsDBNull(i) ? null : rdr.GetValue(i);
            items.Add(row);
        }

        return new ReportResult { Items = items, Total = (int)total, Page = q.Page, PageSize = q.PageSize };
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
                foreach (var k in result.Items[0].Keys) dt.Columns.Add(k);
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
            foreach (var k in result.Items[0].Keys) sb.Append($"<th>{k}</th>");
            sb.Append("</tr>");
            foreach (var row in result.Items)
            {
                sb.Append("<tr>");
                foreach (var v in row.Values) sb.Append($"<td>{v}</td>");
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