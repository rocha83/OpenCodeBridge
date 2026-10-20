using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Entities;
using Rochas.AI.Manager.Domain.Entities.Management;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Application.Services;

public sealed class TrainingWorkflowService(
    IGenericRepository<TrainingWorkflowCard> repo,
    IPersistenceRepository<TrainingWorkflowCard> write,
    IAccuracyEvaluator evaluator,
    string connectionString,
    IGenericRepository<WorkFlowTransition>? transitions = null) : ITrainingWorkflowService
{
    private readonly IGenericRepository<TrainingWorkflowCard> _repo = repo;
    private readonly IPersistenceRepository<TrainingWorkflowCard> _write = write;
    private readonly IAccuracyEvaluator _evaluator = evaluator;
    private readonly string _conn = connectionString;
    private readonly IGenericRepository<WorkFlowTransition>? _transitions = transitions;

    public async Task<int> CreateAsync(string title, string? description, int? experimentId, double targetAccuracy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title is required.", nameof(title));
        var card = new TrainingWorkflowCard
        {
            Title = title.Trim(),
            Description = description?.Trim(),
            ExperimentId = experimentId,
            TargetAccuracy = targetAccuracy <= 0 ? 0.9 : targetAccuracy,
            StatusId = 1,
            Name = title.Trim()
        };
        await _write.Add(card);
        return card.Id;
    }

    public async Task<TrainingWorkflowCard?> GetAsync(int id, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_conn);
        await c.OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, status_id, accuracy FROM training_workflow_cards WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var card = new TrainingWorkflowCard { Id = r.GetInt32(0), StatusId = r.GetInt32(1), Accuracy = r.IsDBNull(2) ? null : r.GetDouble(2) };
        return card;
    }

    public async Task<IReadOnlyList<TrainingWorkflowCard>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.QueryRaw("SELECT * FROM training_workflow_cards", new Dictionary<string, object>());
        return list.OrderBy(c => c.StatusId).ThenBy(c => c.CreatedAt).ToList();
    }

    public async Task MoveAsync(int id, int statusId, CancellationToken ct = default)
    {
        if (statusId <= 0) return;
        // registra transicao antes de estampar novo status
        if (_transitions is not null)
        {
            try
            {
                var card = (await _repo.QueryRaw("SELECT * FROM training_workflow_cards WHERE id=@id", new Dictionary<string, object> { ["@id"] = id })).FirstOrDefault();
                int fromStatus = card?.StatusId ?? 0;
                await _transitions.Add(new WorkFlowTransition(Rochas.AI.Manager.Domain.Helpers.EntityHashCode.Compute(typeof(TrainingWorkflowCard).FullName ?? "TrainingWorkflowCard"), id, fromStatus, statusId, "Move", 0, null));
            }
            catch { }
        }
        using var c = new SqliteConnection(_conn);
        await c.OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE training_workflow_cards SET status_id=@s WHERE id=@id";
        cmd.Parameters.AddWithValue("@s", statusId);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateAccuracyAsync(int id, double accuracy, CancellationToken ct = default)
    {
        using var c = new SqliteConnection(_conn);
        await c.OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE training_workflow_cards SET accuracy=@a WHERE id=@id";
        cmd.Parameters.AddWithValue("@a", accuracy);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AccuracyResult> EvaluateAsync(int id, string expected, string actual, CancellationToken ct = default)
    {
        var card = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Training workflow card {id} not found.");
        var result = _evaluator.Evaluate(expected, actual);
        int newStatus = result.Score >= card.TargetAccuracy ? 3 : 4;
        using var c = new SqliteConnection(_conn);
        await c.OpenAsync(ct);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE training_workflow_cards SET accuracy=@a, status_id=@s WHERE id=@id";
        cmd.Parameters.AddWithValue("@a", result.Score);
        cmd.Parameters.AddWithValue("@s", newStatus);
        cmd.Parameters.AddWithValue("@id", id);
        int rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows == 0) throw new InvalidOperationException($"Update affected 0 rows for card {id}.");
        return result;
    }
    public async Task RejectAsync(int id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Motivo da refutacao e obrigatorio.", nameof(reason));
        var card = (await _repo.QueryRaw("SELECT * FROM training_workflow_cards WHERE id=@id", new Dictionary<string, object> { ["@id"] = id })).FirstOrDefault() ?? throw new KeyNotFoundException($"Card {id} not found.");
        int fromStatus = card.StatusId;
        card.StatusId = 13;
        card.RejectionReason = reason.Trim();
        await _write.Update(card, new TrainingWorkflowCard { Id = id });
        if (_transitions is not null)
        {
            try
            {
                await _transitions.Add(new WorkFlowTransition(Rochas.AI.Manager.Domain.Helpers.EntityHashCode.Compute(typeof(TrainingWorkflowCard).FullName ?? "TrainingWorkflowCard"), id, fromStatus, 13, "Reject", 0, reason.Trim()));
            }
            catch { }
        }
    }
}
