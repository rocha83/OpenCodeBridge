using Rochas.AI.Manager.Domain.Profiles;

namespace Rochas.AI.Manager.Application.Abstractions;

public interface ITrainingPatternService
{
    Task<int> CreateAsync(string name, int? profileId, int? modelArtifactId, string? patternJson, CancellationToken ct = default);
    Task<TrainingPattern?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<TrainingPattern>> ListAsync(CancellationToken ct = default);
}
