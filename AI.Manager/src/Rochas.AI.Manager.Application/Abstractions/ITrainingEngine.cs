namespace Rochas.AI.Manager.Application.Abstractions;

public interface ITrainingEngine
{
    Task RunAsync(int jobId, int? harnessProfileId = null, CancellationToken ct = default);
}
