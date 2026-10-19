using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IHarnessRunService
{
    Task<int> RecordAsync(int evidenceId, int profileVersionId, int modelBindingId, string? requestHash, int toolCalls, int filesCreated, int resultCode, long durationMs, double tokensPerSecond, CancellationToken ct = default);
    Task<HarnessRun?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<HarnessRun>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HarnessRun>> ListByProfileVersionAsync(int profileVersionId, CancellationToken ct = default);
}
