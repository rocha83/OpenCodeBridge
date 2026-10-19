using Rochas.AI.Manager.Domain.Entities;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface ITrainingWorkflowService
{
    Task<int> CreateAsync(string title, string? description, int? experimentId, double targetAccuracy, CancellationToken ct = default);
    Task<TrainingWorkflowCard?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<TrainingWorkflowCard>> ListAsync(CancellationToken ct = default);
    Task MoveAsync(int id, int statusId, CancellationToken ct = default);
    Task UpdateAccuracyAsync(int id, double accuracy, CancellationToken ct = default);
}
