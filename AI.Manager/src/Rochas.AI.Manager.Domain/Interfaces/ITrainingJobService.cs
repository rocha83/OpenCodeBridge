namespace Rochas.AI.Manager.Domain.Interfaces;

public interface ITrainingJobService
{
    Task<int> CreateAsync(int? modelArtifactId, int? datasetId, bool advancedMode, string trainingMethod = "SFT", string? configurationJson = null, CancellationToken ct = default);
    Task<Rochas.AI.Manager.Domain.Training.TrainingJob?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<Rochas.AI.Manager.Domain.Training.TrainingJob>> ListAsync(CancellationToken ct = default);
    Task CancelAsync(int id, CancellationToken ct = default);
    Task StartAsync(int id, CancellationToken ct = default);
    Task ReportProgressAsync(int id, double? lastLoss, double? vramUsedMb, CancellationToken ct = default);
    Task ReportMetricsAsync(int id, int epochsCompleted, double? bestAccuracy, double throughputTokensPerSec, CancellationToken ct = default);
    Task CompleteAsync(int id, CancellationToken ct = default);
    Task FailAsync(int id, string errorMessage, CancellationToken ct = default);
}
