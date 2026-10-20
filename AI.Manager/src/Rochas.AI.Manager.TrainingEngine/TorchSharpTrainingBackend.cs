using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Rochas.AI.Manager.TrainingEngine;

/// <summary>
/// Backend de treinamento em C# puro (sem dependencia nativa de libtorch).
/// Implementa um MLP com 1 camada oculta, ReLU, saida linear, MSE e Adam.
/// Checkpoint serializado em memoria (pesos + vieses).
/// </summary>
public sealed class TorchSharpTrainingBackend : ITrainingBackend
{
    private int _inputSize;
    private int _hiddenSize;
    private int _outputSize;
    private double _lr;
    private double[] _w1 = Array.Empty<double>(); // [hidden x input]
    private double[] _b1 = Array.Empty<double>(); // [hidden]
    private double[] _w2 = Array.Empty<double>(); // [output x hidden]
    private double[] _b2 = Array.Empty<double>(); // [output]
    // Adam state
    private double[] _mW1 = Array.Empty<double>(), _vW1 = Array.Empty<double>();
    private double[] _mB1 = Array.Empty<double>(), _vB1 = Array.Empty<double>();
    private double[] _mW2 = Array.Empty<double>(), _vW2 = Array.Empty<double>();
    private double[] _mB2 = Array.Empty<double>(), _vB2 = Array.Empty<double>();
    private int _t;
    private readonly Random _rng = new(42);
    private const double Beta1 = 0.9, Beta2 = 0.999, Eps = 1e-8;

    public Task InitializeAsync(int inputSize, int hiddenSize, int outputSize, double learningRate, CancellationToken cancellationToken = default)
    {
        _inputSize = inputSize > 0 ? inputSize : 1;
        _hiddenSize = hiddenSize > 0 ? hiddenSize : 1;
        _outputSize = outputSize > 0 ? outputSize : 1;
        _lr = learningRate > 0 ? learningRate : 0.01;
        int n1 = _hiddenSize * _inputSize;
        int n2 = _outputSize * _hiddenSize;
        _w1 = new double[n1]; _b1 = new double[_hiddenSize];
        _w2 = new double[n2]; _b2 = new double[_outputSize];
        _mW1 = new double[n1]; _vW1 = new double[n1];
        _mB1 = new double[_hiddenSize]; _vB1 = new double[_hiddenSize];
        _mW2 = new double[n2]; _vW2 = new double[n2];
        _mB2 = new double[_outputSize]; _vB2 = new double[_outputSize];
        // Xavier init
        double scale1 = Math.Sqrt(2.0 / (_inputSize + _hiddenSize));
        for (int i = 0; i < n1; i++) _w1[i] = (_rng.NextDouble() * 2 - 1) * scale1;
        double scale2 = Math.Sqrt(2.0 / (_hiddenSize + _outputSize));
        for (int i = 0; i < n2; i++) _w2[i] = (_rng.NextDouble() * 2 - 1) * scale2;
        _t = 0;
        return Task.CompletedTask;
    }

    public Task<(double Loss, double Accuracy)> TrainEpochAsync(float[] inputs, float[] targets, int batchSize, CancellationToken cancellationToken = default)
    {
        if (_w1.Length == 0) throw new InvalidOperationException("Backend not initialized.");
        int bs = batchSize > 0 ? batchSize : 1;
        int sampleCount = inputs.Length / (_inputSize > 0 ? _inputSize : 1);
        if (sampleCount == 0) sampleCount = 1;
        double totalLoss = 0;
        int correct = 0, total = 0;
        // Processa em mini-batches
        for (int start = 0; start < sampleCount; start += bs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(start + bs, sampleCount);
            int actual = end - start;
            if (actual <= 0) break;

            // Acumuladores de gradiente para o batch
            var gW1 = new double[_w1.Length];
            var gB1 = new double[_b1.Length];
            var gW2 = new double[_w2.Length];
            var gB2 = new double[_b2.Length];
            double batchLoss = 0;

            for (int s = start; s < end; s++)
            {
                // Forward
                int off = s * _inputSize;
                var hidden = new double[_hiddenSize];
                for (int h = 0; h < _hiddenSize; h++)
                {
                    double sum = _b1[h];
                    for (int i = 0; i < _inputSize; i++) sum += _w1[h * _inputSize + i] * inputs[off + i];
                    hidden[h] = sum > 0 ? sum : 0; // ReLU
                }
                var output = new double[_outputSize];
                for (int o = 0; o < _outputSize; o++)
                {
                    double sum = _b2[o];
                    for (int h = 0; h < _hiddenSize; h++) sum += _w2[o * _hiddenSize + h] * hidden[h];
                    output[o] = sum;
                }
                // Loss MSE
                int toff = s; // targets per sample (1)
                double t = targets.Length > s ? targets[s] : 0;
                double diff = output[0] - t;
                batchLoss += diff * diff;
                // Backward
                // dL/doutput = 2*(pred-target)/outputSize (mean over output)
                var dOut = new double[_outputSize];
                for (int o = 0; o < _outputSize; o++) dOut[o] = 2.0 * (output[o] - t) / _outputSize;
                // grad w2, b2
                for (int o = 0; o < _outputSize; o++)
                {
                    gB2[o] += dOut[o];
                    for (int h = 0; h < _hiddenSize; h++) gW2[o * _hiddenSize + h] += dOut[o] * hidden[h];
                }
                // grad hidden
                var dHidden = new double[_hiddenSize];
                for (int h = 0; h < _hiddenSize; h++)
                {
                    double g = 0;
                    for (int o = 0; o < _outputSize; o++) g += dOut[o] * _w2[o * _hiddenSize + h];
                    dHidden[h] = hidden[h] > 0 ? g : 0; // ReLU deriv
                }
                for (int h = 0; h < _hiddenSize; h++)
                {
                    gB1[h] += dHidden[h];
                    for (int i = 0; i < _inputSize; i++) gW1[h * _inputSize + i] += dHidden[h] * inputs[off + i];
                }

                // accuracy
                if ((output[0] >= 0.5) == (t >= 0.5)) correct++;
                total++;
            }

            totalLoss += batchLoss / actual;

            // Adam update (media do gradiente do batch)
            _t++;
            double biasCorrection1 = 1 - Math.Pow(Beta1, _t);
            double biasCorrection2 = 1 - Math.Pow(Beta2, _t);
            UpdateAdam(_w1, gW1, _mW1, _vW1, actual, biasCorrection1, biasCorrection2);
            UpdateAdam(_b1, gB1, _mB1, _vB1, actual, biasCorrection1, biasCorrection2);
            UpdateAdam(_w2, gW2, _mW2, _vW2, actual, biasCorrection1, biasCorrection2);
            UpdateAdam(_b2, gB2, _mB2, _vB2, actual, biasCorrection1, biasCorrection2);
        }

        double avgLoss = totalLoss / Math.Max(1, (sampleCount + bs - 1) / bs);
        double accuracy = total > 0 ? (double)correct / total : 0.0;
        return Task.FromResult((Math.Round(avgLoss, 4), Math.Round(accuracy, 4)));
    }

    private void UpdateAdam(double[] param, double[] grad, double[] m, double[] v, int batchSize, double bc1, double bc2)
    {
        for (int i = 0; i < param.Length; i++)
        {
            double g = grad[i] / batchSize;
            m[i] = Beta1 * m[i] + (1 - Beta1) * g;
            v[i] = Beta2 * v[i] + (1 - Beta2) * g * g;
            double mHat = m[i] / bc1;
            double vHat = v[i] / bc2;
            param[i] -= _lr * mHat / (Math.Sqrt(vHat) + Eps);
        }
    }

    public Task<double> EvaluateAsync(float[] inputs, float[] targets, CancellationToken cancellationToken = default)
    {
        if (_w1.Length == 0) throw new InvalidOperationException("Backend not initialized.");
        int sampleCount = inputs.Length / _inputSize;
        if (sampleCount == 0) sampleCount = 1;
        int correct = 0, total = 0;
        for (int s = 0; s < sampleCount; s++)
        {
            int off = s * _inputSize;
            var hidden = new double[_hiddenSize];
            for (int h = 0; h < _hiddenSize; h++)
            {
                double sum = _b1[h];
                for (int i = 0; i < _inputSize; i++) sum += _w1[h * _inputSize + i] * inputs[off + i];
                hidden[h] = sum > 0 ? sum : 0;
            }
            double outv = _b2[0];
            for (int h = 0; h < _hiddenSize; h++) outv += _w2[h] * hidden[h];
            double t = targets.Length > s ? targets[s] : 0;
            if ((outv >= 0.5) == (t >= 0.5)) correct++;
            total++;
        }
        return Task.FromResult(total > 0 ? (double)correct / total : 0.0);
    }

    public Task<byte[]> SaveCheckpointAsync(CancellationToken cancellationToken = default)
    {
        if (_w1.Length == 0) throw new InvalidOperationException("Backend not initialized.");
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(_inputSize); bw.Write(_hiddenSize); bw.Write(_outputSize);
        foreach (var v in _w1) bw.Write(v);
        foreach (var v in _b1) bw.Write(v);
        foreach (var v in _w2) bw.Write(v);
        foreach (var v in _b2) bw.Write(v);
        return Task.FromResult(ms.ToArray());
    }
}
