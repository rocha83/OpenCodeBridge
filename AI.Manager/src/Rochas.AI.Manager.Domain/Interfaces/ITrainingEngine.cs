namespace Rochas.AI.Manager.Domain.Interfaces;

public interface ITrainingEngine
{
    Task RunAsync(int jobId, int? harnessProfileId = null, CancellationToken ct = default);
}
