using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Training;

namespace Rochas.AI.Manager.Application.Services;

public sealed class TrainingJobService(
    IGenericRepository<TrainingJob> repo,
    IPersistenceRepository<TrainingJob> write) : ITrainingJobService
{
    private readonly IGenericRepository<TrainingJob> _repo = repo;
    private readonly IPersistenceRepository<TrainingJob> _write = write;

    public async Task<int> CreateAsync(int? modelArtifactId, int? datasetId, bool advancedMode, string trainingMethod = "SFT", string? configurationJson = null, CancellationToken ct = default)
    {
        var job = new TrainingJob
        {
            ModelArtifactId = modelArtifactId,
            DatasetId = datasetId,
            AdvancedMode = advancedMode,
            TrainingMethod = string.IsNullOrWhiteSpace(trainingMethod) ? "SFT" : trainingMethod,
            ConfigurationJson = configurationJson
        };
        await _write.Add(job);
        return job.Id;
    }

    public async Task<TrainingJob?> GetAsync(int id, CancellationToken ct = default)
    {
        var list = await _repo.Query(new TrainingJob { Id = id });
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<TrainingJob>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new TrainingJob());
        return list.OrderByDescending(j => j.CreatedAt).ToList();
    }

    public async Task CancelAsync(int id, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        var old = new TrainingJob(); foreach (var p in typeof(TrainingJob).GetProperties()) p.SetValue(old, p.GetValue(job));
        job.Status = "Cancelled";
        job.CompletedAt = DateTime.UtcNow;
        await _write.Update(job, old);
    }

    public async Task StartAsync(int id, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        if (job.Status != "Queued") throw new InvalidOperationException("Only Queued jobs can start.");
        var old = new TrainingJob(); foreach (var p in typeof(TrainingJob).GetProperties()) p.SetValue(old, p.GetValue(job));
        job.Status = "Running";
        job.StartedAt = DateTime.UtcNow;
        await _write.Update(job, old);
    }

    public async Task ReportProgressAsync(int id, double? lastLoss, double? vramUsedMb, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        if (job.Status != "Running") throw new InvalidOperationException("Only Running jobs can report progress.");
        var old = new TrainingJob(); foreach (var p in typeof(TrainingJob).GetProperties()) p.SetValue(old, p.GetValue(job));
        if (lastLoss.HasValue) job.LastLoss = lastLoss.Value;
        if (vramUsedMb.HasValue) job.VramUsedMb = vramUsedMb.Value;
        await _write.Update(job, old);
    }

    public async Task ReportMetricsAsync(int id, int epochsCompleted, double? bestAccuracy, double throughputTokensPerSec, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        var old = new TrainingJob(); foreach (var p in typeof(TrainingJob).GetProperties()) p.SetValue(old, p.GetValue(job));
        job.EpochsCompleted = epochsCompleted;
        if (bestAccuracy.HasValue) job.BestAccuracy = bestAccuracy.Value;
        job.ThroughputTokensPerSec = throughputTokensPerSec;
        await _write.Update(job, old);
    }

    public async Task CompleteAsync(int id, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        if (job.Status != "Running") throw new InvalidOperationException("Only Running jobs can complete.");
        var old = new TrainingJob(); foreach (var p in typeof(TrainingJob).GetProperties()) p.SetValue(old, p.GetValue(job));
        job.Status = "Completed";
        job.CompletedAt = DateTime.UtcNow;
        await _write.Update(job, old);
    }

    public async Task FailAsync(int id, string errorMessage, CancellationToken ct = default)
    {
        var job = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Training job {id} not found.");
        var old = new TrainingJob(); foreach (var p in typeof(TrainingJob).GetProperties()) p.SetValue(old, p.GetValue(job));
        job.Status = "Failed";
        job.ErrorMessage = errorMessage;
        job.CompletedAt = DateTime.UtcNow;
        await _write.Update(job, old);
    }
}
