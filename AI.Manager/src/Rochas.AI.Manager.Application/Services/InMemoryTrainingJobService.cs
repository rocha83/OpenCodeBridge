namespace Rochas.AI.Manager.Application.Services;

using System.Collections.Concurrent;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Training;

public sealed class InMemoryTrainingJobService : ITrainingJobService
{
    private readonly ConcurrentDictionary<Guid, TrainingJob> _jobs = new();

    public Task<Guid> CreateAsync(Guid? modelArtifactId, Guid? datasetId, bool advancedMode, string? configurationJson, CancellationToken ct = default)
    {
        var job = new TrainingJob
        {
            ModelArtifactId = modelArtifactId,
            DatasetId = datasetId,
            AdvancedMode = advancedMode,
            ConfigurationJson = configurationJson
        };
        _jobs[job.Id] = job;
        return Task.FromResult(job.Id);
    }

    public Task<TrainingJob?> GetAsync(Guid id, CancellationToken ct = default)
    {
        _jobs.TryGetValue(id, out var job);
        return Task.FromResult(job);
    }

    public Task<IReadOnlyList<TrainingJob>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TrainingJob> list = _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();
        return Task.FromResult(list);
    }

    public Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.Status = "Cancelled";
            job.CompletedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }
}
