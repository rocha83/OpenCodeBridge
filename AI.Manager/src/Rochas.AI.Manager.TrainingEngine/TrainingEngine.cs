using System;
using System.Threading;
using System.Threading.Tasks;
using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.TrainingEngine;

public sealed class TrainingEngine(
    ITrainingJobService jobs,
    IModelArtifactService models,
    IDatasetService datasets,
    IHarnessProfileService harness,
    ITrainingBackend backend) : ITrainingEngine
{
    public async Task RunAsync(int jobId, int? harnessProfileId = null, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(jobId, cancellationToken) ?? throw new KeyNotFoundException($"Training job {jobId} not found.");
        await jobs.StartAsync(jobId, cancellationToken);

        string? harnessSummary = null;
        if (harnessProfileId.HasValue)
        {
            var profile = await harness.GetAsync(harnessProfileId.Value, cancellationToken);
            if (profile is not null)
                harnessSummary = $"Harness '{profile.Name}' ({profile.Status}) v{profile.Versions.Count}";
        }

        // Dados sinteticos de treino (substituir por leitura de dataset real)
        int sampleCount = 256;
        var inputs = new float[sampleCount * 4];
        var targets = new float[sampleCount];
        var random = new Random(42);
        for (int index = 0; index < sampleCount; index++)
        {
            inputs[index * 4] = (float)random.NextDouble();
            inputs[index * 4 + 1] = (float)random.NextDouble();
            inputs[index * 4 + 2] = (float)random.NextDouble();
            inputs[index * 4 + 3] = (float)random.NextDouble();
            targets[index] = (inputs[index * 4] + inputs[index * 4 + 1] > 1.0f) ? 1f : 0f;
        }

        await backend.InitializeAsync(4, 16, 1, 0.01, cancellationToken);

        int epochs = 10;
        double bestAccuracy = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int epoch = 1; epoch <= epochs; epoch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (loss, accuracy) = await backend.TrainEpochAsync(inputs, targets, 32, cancellationToken);
            if (accuracy > bestAccuracy) bestAccuracy = accuracy;
            double vram = 4096 + (epoch * 256);
            await jobs.ReportProgressAsync(jobId, loss, vram, cancellationToken);
        }
        stopwatch.Stop();
        double throughput = (sampleCount * epochs) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);

        // Avaliar
        double evaluationAccuracy = await backend.EvaluateAsync(inputs, targets, cancellationToken);
        if (evaluationAccuracy > bestAccuracy) bestAccuracy = evaluationAccuracy;

        // Checkpoint
        var checkpoint = await backend.SaveCheckpointAsync(cancellationToken);

        await jobs.ReportMetricsAsync(jobId, epochs, bestAccuracy, throughput, cancellationToken);
        await jobs.CompleteAsync(jobId, cancellationToken);
    }
}
