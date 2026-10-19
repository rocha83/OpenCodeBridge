namespace Rochas.AI.Manager.Application.Abstractions;

public interface ITrainingJobService
{
    Task<Guid> CreateAsync(Guid? modelArtifactId, Guid? datasetId, bool advancedMode, string? configurationJson, CancellationToken ct = default);
    Task<Rochas.AI.Manager.Domain.Training.TrainingJob?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Rochas.AI.Manager.Domain.Training.TrainingJob>> ListAsync(CancellationToken ct = default);
    Task CancelAsync(Guid id, CancellationToken ct = default);
}
