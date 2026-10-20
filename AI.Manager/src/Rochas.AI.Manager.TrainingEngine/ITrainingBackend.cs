using System.Threading;
using System.Threading.Tasks;

namespace Rochas.AI.Manager.TrainingEngine;

public interface ITrainingBackend
{
    Task InitializeAsync(int inputSize, int hiddenSize, int outputSize, double learningRate, CancellationToken ct = default);
    Task<(double Loss, double Accuracy)> TrainEpochAsync(float[] inputs, float[] targets, int batchSize, CancellationToken ct = default);
    Task<double> EvaluateAsync(float[] inputs, float[] targets, CancellationToken ct = default);
    Task<byte[]> SaveCheckpointAsync(CancellationToken ct = default);
}
