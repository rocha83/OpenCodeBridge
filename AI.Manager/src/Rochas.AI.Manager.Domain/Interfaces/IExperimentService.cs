using Rochas.AI.Manager.Domain.Experiments;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IExperimentService
{
    Task<int> CreateAsync(string name, string? hypothesis, int? baseArtifactId, int? tunedArtifactId, CancellationToken ct = default);
    Task<Experiment?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<Experiment>> ListAsync(CancellationToken ct = default);
    Task StartAsync(int id, CancellationToken ct = default);
    Task CompleteAsync(int id, string? metricsJson, CancellationToken ct = default);
    Task ApproveAsync(int id, CancellationToken ct = default);
    Task RejectAsync(int id, CancellationToken ct = default);
}
