using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Training;

namespace Rochas.AI.Manager.Application.Services;

public sealed class TrainingJobService(
    IGenericRepository<TrainingJob> repo,
    IPersistenceRepository<TrainingJob> write) : ITrainingJobService
{
    private readonly IGenericRepository<TrainingJob> _repo = repo;
    private readonly IPersistenceRepository<TrainingJob> _write = write;

    public async Task<int> CreateAsync(int? modelArtifactId, int? datasetId, bool advancedMode, string? configurationJson, CancellationToken ct = default)
    {
        var job = new TrainingJob
        {
            ModelArtifactId = modelArtifactId,
            DatasetId = datasetId,
            AdvancedMode = advancedMode,
            ConfigurationJson = configurationJson
        };
        await _write.Add(job);
        return job.Id;
    }

    public async Task<TrainingJob?> GetAsync(int id, CancellationToken ct = default) => await _repo.Get(id);

    public async Task<IReadOnlyList<TrainingJob>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.QueryRaw("SELECT * FROM training_jobs", new Dictionary<string, object>());
        return list.OrderByDescending(j => j.CreatedAt).ToList();
    }

    public async Task CancelAsync(int id, CancellationToken ct = default)
    {
        var job = await _repo.Get(id) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        job.Status = "Cancelled";
        job.CompletedAt = DateTime.UtcNow;
        await _write.Update(job, new TrainingJob { Id = id });
    }

    public async Task StartAsync(int id, CancellationToken ct = default)
    {
        var job = await _repo.Get(id) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        if (job.Status != "Queued") throw new InvalidOperationException("Only Queued jobs can start.");
        job.Status = "Running";
        job.StartedAt = DateTime.UtcNow;
        await _write.Update(job, new TrainingJob { Id = id });
    }

    public async Task ReportProgressAsync(int id, double? lastLoss, double? vramUsedMb, CancellationToken ct = default)
    {
        var job = await _repo.Get(id) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        if (job.Status != "Running") throw new InvalidOperationException("Only Running jobs can report progress.");
        if (lastLoss.HasValue) job.LastLoss = lastLoss.Value;
        if (vramUsedMb.HasValue) job.VramUsedMb = vramUsedMb.Value;
        await _write.Update(job, new TrainingJob { Id = id });
    }

    public async Task CompleteAsync(int id, CancellationToken ct = default)
    {
        var job = await _repo.Get(id) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        if (job.Status != "Running") throw new InvalidOperationException("Only Running jobs can complete.");
        job.Status = "Completed";
        job.CompletedAt = DateTime.UtcNow;
        await _write.Update(job, new TrainingJob { Id = id });
    }

    public async Task FailAsync(int id, string errorMessage, CancellationToken ct = default)
    {
        var job = await _repo.Get(id) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        job.Status = "Failed";
        job.ErrorMessage = errorMessage;
        job.CompletedAt = DateTime.UtcNow;
        await _write.Update(job, new TrainingJob { Id = id });
    }
}
