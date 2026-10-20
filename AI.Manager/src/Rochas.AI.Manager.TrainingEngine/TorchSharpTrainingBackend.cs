using System;
using System.Threading;
using System.Threading.Tasks;
using TorchSharp;
using static TorchSharp.torch;

namespace Rochas.AI.Manager.TrainingEngine;

/// <summary>
/// Backend de treinamento baseado em TorchSharp (LibTorch).
/// Implementa um MLP simples com Adam, MSE e checkpoint em memoria.
/// </summary>
public sealed class TorchSharpTrainingBackend : ITrainingBackend
{
    private torch.nn.Module? _model;
    private torch.optim.Optimizer? _optimizer;
    private torch.nn.Module? _loss;

    public Task InitializeAsync(int inputSize, int hiddenSize, int outputSize, double learningRate, CancellationToken ct = default)
    {
        _model = torch.nn.Sequential(
            ("fc1", torch.nn.Linear(inputSize, hiddenSize)),
            ("relu", torch.nn.ReLU()),
            ("fc2", torch.nn.Linear(hiddenSize, outputSize)));
        _optimizer = torch.optim.Adam(_model.parameters(), lr: learningRate);
        _loss = torch.nn.MSELoss();
        return Task.CompletedTask;
    }

    public Task<(double Loss, double Accuracy)> TrainEpochAsync(float[] inputs, float[] targets, int batchSize, CancellationToken ct = default)
    {
        if (_model is null || _optimizer is null || _loss is null) throw new InvalidOperationException("Backend not initialized.");

        _model.train();
        int n = inputs.Length / (batchSize > 0 ? batchSize : 1);
        if (n == 0) n = 1;
        int features = inputs.Length / (inputs.Length / Math.Max(1, batchSize));
        if (features == 0) features = 1;

        double totalLoss = 0;
        int correct = 0;
        int total = 0;

        for (int b = 0; b < n; b++)
        {
            ct.ThrowIfCancellationRequested();
            int start = b * batchSize;
            int end = Math.Min(start + batchSize, inputs.Length);
            int bs = end - start;
            if (bs <= 0) break;

            using var x = torch.tensor(inputs[start..end]).reshape(bs, -1);
            using var y = torch.tensor(targets[start..end]).reshape(bs, -1);

            _optimizer.zero_grad();
            using var pred = ((dynamic)_model!).forward(x);
            using var l = ((dynamic)_loss!).forward(pred, y);
            l.backward();
            _optimizer.step();

            totalLoss += l.item<float>();
            // accuracy: pred mais proximo do target (argmax para classificacao binaria simples)
            for (int i = 0; i < bs; i++)
            {
                float p = pred[i].item<float>();
                float t = y[i].item<float>();
                if ((p >= 0.5f) == (t >= 0.5f)) correct++;
                total++;
            }
        }

        double avgLoss = totalLoss / Math.Max(1, n);
        double acc = total > 0 ? (double)correct / total : 0.0;
        return Task.FromResult((Math.Round(avgLoss, 4), Math.Round(acc, 4)));
    }

    public Task<double> EvaluateAsync(float[] inputs, float[] targets, CancellationToken ct = default)
    {
        if (_model is null) throw new InvalidOperationException("Backend not initialized.");
        _model.eval();
        using var x = torch.tensor(inputs).reshape(-1, inputs.Length / Math.Max(1, targets.Length));
        using var y = torch.tensor(targets).reshape(-1, 1);
        using var pred = ((dynamic)_model!).forward(x);
        int correct = 0, total = 0;
        for (int i = 0; i < pred.shape[0]; i++)
        {
            if ((pred[i].item<float>() >= 0.5f) == (y[i].item<float>() >= 0.5f)) correct++;
            total++;
        }
        return Task.FromResult(total > 0 ? (double)correct / total : 0.0);
    }

    public Task<byte[]> SaveCheckpointAsync(CancellationToken ct = default)
    {
        if (_model is null) throw new InvalidOperationException("Backend not initialized.");
        using var ms = new System.IO.MemoryStream();
        _model.save(ms);
        return Task.FromResult(ms.ToArray());
    }
}
