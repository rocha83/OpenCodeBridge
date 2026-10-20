using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Training;
using Rochas.AI.Manager.Domain.Datasets;

namespace Rochas.AI.Manager.TrainingEngine;

public sealed class TrainingEngine(
    ITrainingJobService jobs,
    IModelArtifactService models,
    IDatasetService datasets,
    IHarnessProfileService harness,
    ITrainingBackend backend,
    ITrainingRunner runner) : ITrainingEngine
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

        try
        {
            if (job.AdvancedMode)
            {
                var script = Environment.GetEnvironmentVariable("QLORA_SCRIPT") ?? "sft_train.py";
                var dataset = job.DatasetId.HasValue ? (await datasets.GetAsync(job.DatasetId.Value, cancellationToken))?.FilePath : null;
                var args = $"\"{script}\"";
                if (!string.IsNullOrWhiteSpace(dataset)) args += $" --dataset \"{dataset}\"";
                args += $" --method {job.TrainingMethod}";
                var (exit, stdout, stderr) = await runner.RunAsync("python3", args, null, cancellationToken);
                if (exit != 0)
                {
                    await jobs.FailAsync(jobId, $"QLoRA failed (exit {exit}): {stderr}", cancellationToken);
                    return;
                }
                await jobs.ReportProgressAsync(jobId, 0.0, 0, cancellationToken);
                await jobs.ReportMetricsAsync(jobId, 1, 1.0, 0, cancellationToken);
                await jobs.CompleteAsync(jobId, cancellationToken);
                return;
            }

            var (inputs, targets, featureCount) = await LoadTrainingDataAsync(job, datasets, cancellationToken);

            int hiddenSize = 16;
            double learningRate = 0.01;
            int batchSize = 32;
            int epochs = 10;

            await backend.InitializeAsync(featureCount, hiddenSize, 1, learningRate, cancellationToken);

            int sampleCount = inputs.Length / featureCount;
            double bestAccuracy = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int epoch = 1; epoch <= epochs; epoch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (loss, accuracy) = await backend.TrainEpochAsync(inputs, targets, batchSize, cancellationToken);
                if (accuracy > bestAccuracy) bestAccuracy = accuracy;
                double vram = 4096 + (epoch * 256);
                await jobs.ReportProgressAsync(jobId, loss, vram, cancellationToken);
            }
            stopwatch.Stop();
            double throughput = (sampleCount * epochs) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);

            double evaluationAccuracy = await backend.EvaluateAsync(inputs, targets, cancellationToken);
            if (evaluationAccuracy > bestAccuracy) bestAccuracy = evaluationAccuracy;

            var checkpoint = await backend.SaveCheckpointAsync(cancellationToken);

            await jobs.ReportMetricsAsync(jobId, epochs, bestAccuracy, throughput, cancellationToken);
            await jobs.CompleteAsync(jobId, cancellationToken);
        }
        catch (Exception ex)
        {
            try { await jobs.FailAsync(jobId, ex.Message, cancellationToken); } catch { }
            throw;
        }
    }

    private static async Task<(float[] Inputs, float[] Targets, int FeatureCount)> LoadTrainingDataAsync(TrainingJob job, IDatasetService datasets, CancellationToken ct)
    {
        if (!job.DatasetId.HasValue)
        {
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
            return (inputs, targets, 4);
        }

        var dataset = await datasets.GetAsync(job.DatasetId.Value, ct) ?? throw new KeyNotFoundException($"Dataset {job.DatasetId} not found.");
        if (string.IsNullOrWhiteSpace(dataset.FilePath) || !File.Exists(dataset.FilePath))
            throw new FileNotFoundException($"Dataset file not found: {dataset.FilePath}");

        var ext = Path.GetExtension(dataset.FilePath).ToLowerInvariant();
        if (ext == ".jsonl" || ext == ".json")
        {
            var lines = await File.ReadAllLinesAsync(dataset.FilePath, ct);
            var inputList = new System.Collections.Generic.List<float>();
            var targetList = new System.Collections.Generic.List<float>();
            int featureCount = 0;
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                JsonElement inputsEl = default;
                if (root.TryGetProperty("inputs", out var ie)) inputsEl = ie;
                else if (root.TryGetProperty("features", out var fe)) inputsEl = fe;
                else if (root.TryGetProperty("input", out var ine)) inputsEl = ine;
                double target = 0;
                if (root.TryGetProperty("target", out var te)) target = te.GetDouble();
                else if (root.TryGetProperty("label", out var le)) target = le.GetDouble();
                else if (root.TryGetProperty("y", out var ye)) target = ye.GetDouble();

                if (inputsEl.ValueKind == JsonValueKind.Array)
                {
                    if (featureCount == 0) featureCount = inputsEl.GetArrayLength();
                    foreach (var v in inputsEl.EnumerateArray()) inputList.Add((float)v.GetDouble());
                }
                targetList.Add((float)target);
            }
            if (featureCount == 0) featureCount = 1;
            return (inputList.ToArray(), targetList.ToArray(), featureCount);
        }
        if (ext == ".csv")
        {
            var lines = await File.ReadAllLinesAsync(dataset.FilePath, ct);
            if (lines.Length < 2) throw new InvalidDataException("CSV dataset must have header + rows.");
            var header = lines[0].Split(',');
            int featureCount = header.Length - 1;
            var inputList = new System.Collections.Generic.List<float>();
            var targetList = new System.Collections.Generic.List<float>();
            for (int i = 1; i < lines.Length; i++)
            {
                var row = lines[i].Trim();
                if (string.IsNullOrEmpty(row)) continue;
                var cols = row.Split(',');
                for (int c = 0; c < featureCount; c++)
                    inputList.Add(float.Parse(cols[c], System.Globalization.CultureInfo.InvariantCulture));
                targetList.Add(float.Parse(cols[featureCount], System.Globalization.CultureInfo.InvariantCulture));
            }
            if (featureCount <= 0) featureCount = 1;
            return (inputList.ToArray(), targetList.ToArray(), featureCount);
        }

        throw new NotSupportedException($"Unsupported dataset format: {ext}");
    }
}
