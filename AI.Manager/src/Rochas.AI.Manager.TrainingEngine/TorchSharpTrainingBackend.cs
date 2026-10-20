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
    private torch.nn.Module? model;
    private torch.optim.Optimizer? optimizer;
    private torch.nn.Module? lossFunction;

    public Task InitializeAsync(int inputSize, int hiddenSize, int outputSize, double learningRate, CancellationToken cancellationToken = default)
    {
        model = torch.nn.Sequential(
            ("fc1", torch.nn.Linear(inputSize, hiddenSize)),
            ("relu", torch.nn.ReLU()),
            ("fc2", torch.nn.Linear(hiddenSize, outputSize)));
        optimizer = torch.optim.Adam(model.parameters(), lr: learningRate);
        lossFunction = torch.nn.MSELoss();
        return Task.CompletedTask;
    }

    public Task<(double Loss, double Accuracy)> TrainEpochAsync(float[] inputs, float[] targets, int batchSize, CancellationToken cancellationToken = default)
    {
        if (model is null || optimizer is null || lossFunction is null) throw new InvalidOperationException("Backend not initialized.");

        model.train();
        int batchCount = inputs.Length / (batchSize > 0 ? batchSize : 1);
        if (batchCount == 0) batchCount = 1;
        int features = inputs.Length / (inputs.Length / Math.Max(1, batchSize));
        if (features == 0) features = 1;

        double totalLoss = 0;
        int correct = 0;
        int total = 0;

        for (int batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = batchIndex * batchSize;
            int end = Math.Min(start + batchSize, inputs.Length);
            int actualBatchSize = end - start;
            if (actualBatchSize <= 0) break;

            using var inputTensor = torch.tensor(inputs[start..end]).reshape(actualBatchSize, -1);
            using var targetTensor = torch.tensor(targets[start..end]).reshape(actualBatchSize, -1);

            optimizer.zero_grad();
            using var prediction = ((dynamic)model!).forward(inputTensor);
            using var lossTensor = ((dynamic)lossFunction!).forward(prediction, targetTensor);
            lossTensor.backward();
            optimizer.step();

            totalLoss += lossTensor.item<float>();
            for (int index = 0; index < actualBatchSize; index++)
            {
                float predicted = prediction[index].item<float>();
                float target = targetTensor[index].item<float>();
                if ((predicted >= 0.5f) == (target >= 0.5f)) correct++;
                total++;
            }
        }

        double averageLoss = totalLoss / Math.Max(1, batchCount);
        double accuracy = total > 0 ? (double)correct / total : 0.0;
        return Task.FromResult((Math.Round(averageLoss, 4), Math.Round(accuracy, 4)));
    }

    public Task<double> EvaluateAsync(float[] inputs, float[] targets, CancellationToken cancellationToken = default)
    {
        if (model is null) throw new InvalidOperationException("Backend not initialized.");
        model.eval();
        using var inputTensor = torch.tensor(inputs).reshape(-1, inputs.Length / Math.Max(1, targets.Length));
        using var targetTensor = torch.tensor(targets).reshape(-1, 1);
        using var prediction = ((dynamic)model!).forward(inputTensor);
        int correct = 0, total = 0;
        for (int index = 0; index < prediction.shape[0]; index++)
        {
            if ((prediction[index].item<float>() >= 0.5f) == (targetTensor[index].item<float>() >= 0.5f)) correct++;
            total++;
        }
        return Task.FromResult(total > 0 ? (double)correct / total : 0.0);
    }

    public Task<byte[]> SaveCheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (model is null) throw new InvalidOperationException("Backend not initialized.");
        using var memoryStream = new System.IO.MemoryStream();
        model.save(memoryStream);
        return Task.FromResult(memoryStream.ToArray());
    }
}
