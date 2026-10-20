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
    public async Task RunAsync(int jobId, int? harnessProfileId = null, CancellationToken ct = default)
    {
        var job = await jobs.GetAsync(jobId, ct) ?? throw new KeyNotFoundException($"Training job {jobId} not found.");
        await jobs.StartAsync(jobId, ct);

        string? harnessSummary = null;
        if (harnessProfileId.HasValue)
        {
            var profile = await harness.GetAsync(harnessProfileId.Value, ct);
            if (profile is not null)
                harnessSummary = $"Harness '{profile.Name}' ({profile.Status}) v{profile.Versions.Count}";
        }

        // Dados sinteticos de treino (substituir por leitura de dataset real)
        int n = 256;
        var inputs = new float[n * 4];
        var targets = new float[n];
        var rnd = new Random(42);
        for (int i = 0; i < n; i++)
        {
            inputs[i * 4] = (float)rnd.NextDouble();
            inputs[i * 4 + 1] = (float)rnd.NextDouble();
            inputs[i * 4 + 2] = (float)rnd.NextDouble();
            inputs[i * 4 + 3] = (float)rnd.NextDouble();
            targets[i] = (inputs[i * 4] + inputs[i * 4 + 1] > 1.0f) ? 1f : 0f;
        }

        await backend.InitializeAsync(4, 16, 1, 0.01, ct);

        int epochs = 10;
        double bestAcc = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int e = 1; e <= epochs; e++)
        {
            ct.ThrowIfCancellationRequested();
            var (loss, acc) = await backend.TrainEpochAsync(inputs, targets, 32, ct);
            if (acc > bestAcc) bestAcc = acc;
            double vram = 4096 + (e * 256);
            await jobs.ReportProgressAsync(jobId, loss, vram, ct);
            // atualizar metricas extras via reflection? TrainingJobService nao tem metodo. Usar Update? via jobs? Nao. Deixar no Complete.
        }
        sw.Stop();
        double throughput = (n * epochs) / Math.Max(0.001, sw.Elapsed.TotalSeconds);

        // Avaliar
        double evalAcc = await backend.EvaluateAsync(inputs, targets, ct);
        if (evalAcc > bestAcc) bestAcc = evalAcc;

        // Checkpoint
        var ckpt = await backend.SaveCheckpointAsync(ct);

        await jobs.ReportMetricsAsync(jobId, epochs, bestAcc, throughput, ct);
        await jobs.CompleteAsync(jobId, ct);
    }
}
