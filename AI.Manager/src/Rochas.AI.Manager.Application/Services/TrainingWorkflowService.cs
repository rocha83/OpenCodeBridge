using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Entities;
using Rochas.AI.Manager.Domain.Entities.Management;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Application.Services;

public sealed class TrainingWorkflowService(
    IGenericRepository<TrainingWorkflowCard> repo,
    IPersistenceRepository<TrainingWorkflowCard> write,
    IAccuracyEvaluator evaluator,
    IGenericRepository<WorkFlowTransition>? transitions = null) : ITrainingWorkflowService
{
    private readonly IGenericRepository<TrainingWorkflowCard> _repo = repo;
    private readonly IPersistenceRepository<TrainingWorkflowCard> _write = write;
    private readonly IAccuracyEvaluator _evaluator = evaluator;
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
        var list = await _repo.Query(new TrainingWorkflowCard { Id = id });
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<TrainingWorkflowCard>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new TrainingWorkflowCard());
        return list.OrderBy(c => c.StatusId).ThenBy(c => c.CreatedAt).ToList();
    }

    public async Task MoveAsync(int id, int statusId, CancellationToken ct = default)
    {
        if (statusId <= 0) return;
        var card = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Card {id} not found.");
        int fromStatus = card.StatusId;
        if (_transitions is not null)
        {
            try
            {
                await _transitions.Add(new WorkFlowTransition(Rochas.AI.Manager.Domain.Helpers.EntityHashCode.Compute(typeof(TrainingWorkflowCard).FullName ?? "TrainingWorkflowCard"), id, fromStatus, statusId, "Move", 0, null));
            }
            catch { }
        }
        var old = new TrainingWorkflowCard(); foreach (var p in typeof(TrainingWorkflowCard).GetProperties()) p.SetValue(old, p.GetValue(card));
        card.StatusId = statusId;
        await _write.Update(card, old);
    }

    public async Task UpdateAccuracyAsync(int id, double accuracy, CancellationToken ct = default)
    {
        var card = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Card {id} not found.");
        var old = new TrainingWorkflowCard(); foreach (var p in typeof(TrainingWorkflowCard).GetProperties()) p.SetValue(old, p.GetValue(card));
        card.Accuracy = accuracy;
        await _write.Update(card, old);
    }

    public async Task<AccuracyResult> EvaluateAsync(int id, string expected, string actual, CancellationToken ct = default)
    {
        var card = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Training workflow card {id} not found.");
        var result = _evaluator.Evaluate(expected, actual);
        int newStatus = result.Score >= card.TargetAccuracy ? 3 : 4;
        var old = new TrainingWorkflowCard(); foreach (var p in typeof(TrainingWorkflowCard).GetProperties()) p.SetValue(old, p.GetValue(card));
        card.Accuracy = result.Score;
        card.StatusId = newStatus;
        await _write.Update(card, old);
        return result;
    }

    public async Task RejectAsync(int id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Motivo da refutacao e obrigatorio.", nameof(reason));
        var card = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Card {id} not found.");
        int fromStatus = card.StatusId;
        var old = new TrainingWorkflowCard(); foreach (var p in typeof(TrainingWorkflowCard).GetProperties()) p.SetValue(old, p.GetValue(card));
        card.StatusId = 13;
        card.RejectionReason = reason.Trim();
        await _write.Update(card, old);
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
