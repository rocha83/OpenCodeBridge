using Rochas.AI.Manager.Application.Abstractions;

namespace Rochas.AI.Manager.TrainingEngine;

public sealed class TrainingEngine(
    ITrainingJobService jobs,
    IModelArtifactService models,
    IDatasetService datasets,
    IHarnessProfileService harness) : ITrainingEngine
{
    public async Task RunAsync(int jobId, int? harnessProfileId = null, CancellationToken ct = default)
    {
        var job = await jobs.GetAsync(jobId, ct) ?? throw new KeyNotFoundException($"Training job {jobId} not found.");
        await jobs.StartAsync(jobId, ct);

        // Ativa influencia de harness cadastrado na plataforma
        string? harnessSummary = null;
        if (harnessProfileId.HasValue)
        {
            var profile = await harness.GetAsync(harnessProfileId.Value, ct);
            if (profile is not null)
            {
                harnessSummary = $"Harness '{profile.Name}' ({profile.Status}) v{profile.Versions.Count}";
            }
        }

        int epochs = 5;
        for (int i = 1; i <= epochs; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct);
            double loss = Math.Round(1.0 / (i + 1), 4);
            double vram = 4096 + (i * 256);
            await jobs.ReportProgressAsync(jobId, loss, vram, ct);
        }

        await jobs.CompleteAsync(jobId, ct);
    }
}
