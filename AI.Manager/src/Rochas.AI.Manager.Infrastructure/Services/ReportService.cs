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
using Rochas.AI.Manager.Domain.Reporting;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.ExcelToJson;
using Rochas.PDFGenerator;
using Rochas.PDFGenerator.Helpers;

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
        ["Agent"] = "agents",
        ["WorkFlowTransition"] = "workflow_transitions"
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
        ["Agent"] = typeof(Rochas.AI.Manager.Domain.Entities.Agent),
        ["WorkFlowTransition"] = typeof(Rochas.AI.Manager.Domain.Entities.Management.WorkFlowTransition)
    };

    private static readonly string[] DefaultAxes = ["created_at"];

    private static readonly Dictionary<string, string[]> AllowedDateAxes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TrainingJob"] = ["created_at", "started_at", "completed_at"],
        ["TrainingWorkflowCard"] = ["created_at", "updated_at"]
    };

    private static readonly Dictionary<string, string> DateAxisProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["created_at"] = "CreatedAt",
        ["started_at"] = "StartedAt",
        ["completed_at"] = "CompletedAt",
        ["updated_at"] = "UpdatedAt",
        ["approved_at"] = "ApprovedAt"
    };

    private static readonly Dictionary<string, string> AccuracyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TrainingJob"] = "BestAccuracy",
        ["TrainingWorkflowCard"] = "Accuracy"
    };

    private static readonly Dictionary<string, string> ModeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Agent"] = "Mode"
    };

    private static readonly Dictionary<string, string[]> UserProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TrainingWorkflowCard"] = ["RequesterId", "ApproverId", "UpdaterId"]
    };

    private sealed record FamilySource(string[] ArtifactIdProperties, bool OwnFamily);

    private static readonly Dictionary<string, FamilySource> FamilySources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TrainingJob"] = new(["ModelArtifactId"], false),
        ["Experiment"] = new(["BaseArtifactId", "TunedArtifactId"], false),
        ["ModelArtifact"] = new([], true)
    };

    private static readonly Dictionary<string, (double Min, double Max)> AccuracyBands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["low"] = (double.NegativeInfinity, 0.5),
        ["medium"] = (0.5, 0.8),
        ["high"] = (0.8, double.PositiveInfinity)
    };

    public async Task<ReportResult> QueryAsync(ReportQuery q, CancellationToken ct = default)
    {
        var resolvedEntity = q.Entity;
        var def = ReportCatalog.Find(q.Entity);
        if (def is not null) resolvedEntity = def.Entity;
        if (!TypeMap.TryGetValue(resolvedEntity, out var type) || !TableMap.ContainsKey(resolvedEntity))
            throw new ReportQueryException($"Entidade de relatorio desconhecida: '{q.Entity}'.");
        q.Entity = resolvedEntity;

        var page = q.Page < 1 ? 1 : q.Page;
        var pageSize = q.PageSize < 1 ? 20 : Math.Min(q.PageSize, 500);
        var dateAxis = string.IsNullOrWhiteSpace(q.DateAxis) ? "created_at" : q.DateAxis.Trim().ToLowerInvariant();
        var allowedAxes = AllowedDateAxes.TryGetValue(q.Entity, out var axes) ? axes : DefaultAxes;
        if (!allowedAxes.Contains(dateAxis, StringComparer.OrdinalIgnoreCase))
            throw new ReportQueryException($"Eixo temporal '{dateAxis}' nao suportado para {q.Entity}. Use: {string.Join(", ", allowedAxes)}.");

        var datePropertyName = DateAxisProperties[dateAxis];
        if (type.GetProperty(datePropertyName) is null)
            throw new ReportQueryException($"Eixo temporal '{dateAxis}' nao existe em {q.Entity}.");

        if (q.AccuracyMin.HasValue && q.AccuracyMax.HasValue && q.AccuracyMin > q.AccuracyMax)
            throw new ReportQueryException("AccuracyMin nao pode ser maior que AccuracyMax.");
        if (!string.IsNullOrWhiteSpace(q.AccuracyBand) && !AccuracyBands.ContainsKey(q.AccuracyBand))
            throw new ReportQueryException($"Banda de acuracia invalida: '{q.AccuracyBand}'. Use: low, medium, high.");
        if ((!string.IsNullOrWhiteSpace(q.AccuracyBand) || q.AccuracyMin.HasValue || q.AccuracyMax.HasValue)
            && !AccuracyProperties.ContainsKey(q.Entity))
            throw new ReportQueryException($"{q.Entity} nao possui metrica de acuracia para filtrar.");
        if (!string.IsNullOrWhiteSpace(q.ModelFamily) && !FamilySources.ContainsKey(q.Entity))
            throw new ReportQueryException($"{q.Entity} nao possui vinculo de modelo para filtrar por familia.");
        if (!string.IsNullOrWhiteSpace(q.Mode) && !ModeProperties.ContainsKey(q.Entity))
            throw new ReportQueryException($"{q.Entity} nao possui coluna de modo para filtrar.");
        if (q.UserId.HasValue && !UserProperties.ContainsKey(q.Entity))
            throw new ReportQueryException($"{q.Entity} nao possui vinculo de usuario para filtrar.");
        if (q.AgentId.HasValue)
            throw new ReportQueryException("Filtro AgentId indisponivel: o modelo atual nao vincula runs/capturas a agentes.");

        if (resolvedEntity.Equals("TrainingJob", StringComparison.OrdinalIgnoreCase))
        {
            return await QueryTrainingJobSqlAsync(q, type, page, pageSize, ct);
        }

        var list = await LoadAsync(type, ct);
        var artifacts = await LoadArtifactFamiliesAsync(ct);
        var statuses = await LoadStatusIdsAsync(ct);

        var accuracyProperty = AccuracyProperties.TryGetValue(q.Entity, out var accuracyProp) ? accuracyProp : null;
        var userPropertyNames = UserProperties.TryGetValue(q.Entity, out var userProps) ? userProps : Array.Empty<string>();
        var modePropertyName = ModeProperties.TryGetValue(q.Entity, out var modeProp) ? modeProp : null;
        var familySource = FamilySources.TryGetValue(q.Entity, out var source) ? source : null;
        var familyFilter = string.IsNullOrWhiteSpace(q.ModelFamily) ? null : q.ModelFamily.Trim();

        var items = new List<Dictionary<string, object?>>();
        foreach (var obj in list)
        {
            var row = new Dictionary<string, object?>();
            foreach (var prop in type.GetProperties())
            {
                var colAttr = prop.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.ColumnAttribute>();
                row[colAttr?.Name ?? prop.Name] = prop.GetValue(obj);
            }

            if (!MatchesStatus(obj, type, q.Status, statuses)) continue;
            if (!MatchesDate(obj, type, datePropertyName, q.DateFrom, q.DateTo)) continue;
            if (accuracyProperty is not null && !MatchesAccuracy(obj, type, accuracyProperty, q)) continue;
            if (familyFilter is not null && !MatchesFamily(obj, type, familySource!, familyFilter, artifacts)) continue;
            if (modePropertyName is not null
                && !string.Equals(type.GetProperty(modePropertyName)!.GetValue(obj)?.ToString(), q.Mode, StringComparison.OrdinalIgnoreCase)) continue;
            if (q.UserId.HasValue && !userPropertyNames.Any(name => Convert.ToInt32(type.GetProperty(name)?.GetValue(obj) ?? 0) == q.UserId.Value)) continue;

            items.Add(row);
        }

        var total = items.Count;
        var pageItems = items.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new ReportResult { Items = pageItems, Total = total, Page = page, PageSize = pageSize };
    }

    private async Task<List<object>> LoadAsync(Type type, CancellationToken ct)
    {
        var repoType = typeof(Rochas.Data.Specification.Interfaces.IGenericRepository<>).MakeGenericType(type);
        var repo = sp.GetRequiredService(repoType);
        var queryMethod = repoType.GetMethod("Query", new[] { type, typeof(bool), typeof(bool) })!;
        var task = queryMethod.Invoke(repo, new[] { Activator.CreateInstance(type), false, false })!;
        var awaiter = task.GetType().GetMethod("GetAwaiter")!.Invoke(task, null)!;
        var result = awaiter.GetType().GetMethod("GetResult")!.Invoke(awaiter, null)!;
        return ((System.Collections.IEnumerable)result).Cast<object>().ToList();
    }

    private async Task<Dictionary<int, string>> LoadArtifactFamiliesAsync(CancellationToken ct)
    {
        var repoType = typeof(Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Models.ModelArtifact>);
        if (sp.GetService(repoType) is not Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Models.ModelArtifact> repo)
            return new Dictionary<int, string>();
        var artifacts = await repo.Query(new Rochas.AI.Manager.Domain.Models.ModelArtifact());
        return artifacts
            .GroupBy(a => a.Id)
            .ToDictionary(g => g.Key, g => g.First().Family ?? string.Empty);
    }

    private async Task<Dictionary<string, int>> LoadStatusIdsAsync(CancellationToken ct)
    {
        var repoType = typeof(Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Entities.Management.EntityStatus>);
        if (sp.GetService(repoType) is not Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Entities.Management.EntityStatus> repo)
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var statuses = await repo.Query(new Rochas.AI.Manager.Domain.Entities.Management.EntityStatus());
        return statuses
            .Where(s => !string.IsNullOrWhiteSpace(s.Code))
            .GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
    }

    private static bool MatchesStatus(object obj, Type type, string? status, IReadOnlyDictionary<string, int> statuses)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        var statusProp = type.GetProperty("Status");
        if (statusProp is not null)
            return string.Equals(statusProp.GetValue(obj)?.ToString(), status, StringComparison.OrdinalIgnoreCase);
        var idProp = type.GetProperty("StatusId");
        if (idProp is null) return true;
        var id = Convert.ToInt32(idProp.GetValue(obj) ?? 0);
        if (int.TryParse(status, out var parsed)) return id == parsed;
        return statuses.TryGetValue(status.Trim(), out var resolved) && resolved == id;
    }

    private static bool MatchesDate(object obj, Type type, string propertyName, DateTime? from, DateTime? to)
    {
        if (!from.HasValue && !to.HasValue) return true;
        if (type.GetProperty(propertyName)?.GetValue(obj) is not DateTime value) return false;
        // Intervalo half-open em UTC: [from 00:00, to+1 dia)
        if (from.HasValue)
        {
            var fromStart = from.Value.Date;
            if (value < fromStart) return false;
        }
        if (to.HasValue)
        {
            var toExclusive = to.Value.Date.AddDays(1);
            if (value >= toExclusive) return false;
        }
        return true;
    }

    private static bool MatchesAccuracy(object obj, Type type, string propertyName, ReportQuery q)
    {
        var raw = type.GetProperty(propertyName)?.GetValue(obj);
        if (raw is null) return false;
        var score = Convert.ToDouble(raw);
        if (q.AccuracyMin.HasValue && score < q.AccuracyMin.Value) return false;
        if (q.AccuracyMax.HasValue && score > q.AccuracyMax.Value) return false;
        if (!string.IsNullOrWhiteSpace(q.AccuracyBand))
        {
            var (min, max) = AccuracyBands[q.AccuracyBand];
            if (score < min || score >= max) return false;
        }
        return true;
    }

    private static bool MatchesFamily(object obj, Type type, FamilySource source, string family, IReadOnlyDictionary<int, string> artifacts)
    {
        if (source.OwnFamily)
            return string.Equals(type.GetProperty("Family")?.GetValue(obj)?.ToString(), family, StringComparison.OrdinalIgnoreCase);
        foreach (var propertyName in source.ArtifactIdProperties)
        {
            var raw = type.GetProperty(propertyName)?.GetValue(obj);
            if (raw is null) continue;
            var id = Convert.ToInt32(raw);
            if (artifacts.TryGetValue(id, out var artifactFamily)
                && string.Equals(artifactFamily, family, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public IReadOnlyList<ReportDefinition> GetCatalog() => ReportCatalog.All;



    private async Task<ReportResult> QueryTrainingJobSqlAsync(ReportQuery q, Type type, int page, int pageSize, CancellationToken ct)
    {
        var dateCol = string.IsNullOrWhiteSpace(q.DateAxis) ? "created_at" : q.DateAxis.Trim().ToLowerInvariant();
        var allowed = new[] { "created_at", "started_at", "completed_at" };
        if (!allowed.Contains(dateCol)) throw new ReportQueryException($"Eixo temporal '{dateCol}' nao suportado para TrainingJob. Use: {string.Join(", ", allowed)}.");

        var where = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (q.DateFrom.HasValue)
        {
            where.Add($"{dateCol} >= @fromStart");
            parameters.Add(("@fromStart", q.DateFrom.Value.Date.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        if (q.DateTo.HasValue)
        {
            where.Add($"{dateCol} < @toExclusive");
            parameters.Add(("@toExclusive", q.DateTo.Value.Date.AddDays(1).ToString("yyyy-MM-dd HH:mm:ss")));
        }
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            where.Add("status = @status");
            parameters.Add(("@status", q.Status.Trim()));
        }
        if (q.AccuracyMin.HasValue)
        {
            where.Add("best_accuracy >= @accMin");
            parameters.Add(("@accMin", q.AccuracyMin.Value));
        }
        if (q.AccuracyMax.HasValue)
        {
            where.Add("best_accuracy <= @accMax");
            parameters.Add(("@accMax", q.AccuracyMax.Value));
        }
        if (!string.IsNullOrWhiteSpace(q.AccuracyBand) && AccuracyBands.TryGetValue(q.AccuracyBand, out var band))
        {
            where.Add("best_accuracy >= @bMin AND best_accuracy < @bMax");
            parameters.Add(("@bMin", double.IsNegativeInfinity(band.Min) ? -1d : band.Min));
            parameters.Add(("@bMax", double.IsPositiveInfinity(band.Max) ? 2d : band.Max));
        }
        if (!string.IsNullOrWhiteSpace(q.ModelFamily))
        {
            where.Add("EXISTS (SELECT 1 FROM model_artifacts a WHERE a.id = model_artifact_id AND LOWER(COALESCE(a.family,'')) = LOWER(@family))");
            parameters.Add(("@family", q.ModelFamily.Trim()));
        }

        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(Rochas.AI.Manager.Infrastructure.Data.AppDb.ConnectionString);
        await conn.OpenAsync(ct);

        var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM training_jobs {whereSql}";
        foreach (var (name, value) in parameters)
        {
            countCmd.Parameters.AddWithValue(name, value);
        }
        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        var offset = (page - 1) * pageSize;
        var selectCmd = conn.CreateCommand();
        selectCmd.CommandText = $"SELECT * FROM training_jobs {whereSql} ORDER BY {dateCol} LIMIT @limit OFFSET @offset";
        foreach (var (name, value) in parameters)
        {
            selectCmd.Parameters.AddWithValue(name, value);
        }
        selectCmd.Parameters.AddWithValue("@limit", pageSize);
        selectCmd.Parameters.AddWithValue("@offset", offset);

        var items = new List<Dictionary<string, object?>>();
        using var reader = await selectCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var col = reader.GetName(i);
                var val = reader.IsDBNull(i) ? null : reader.GetValue(i);
                row[col] = val;
            }
            items.Add(row);
        }

        return new ReportResult { Items = items, Total = total, Page = page, PageSize = pageSize };
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