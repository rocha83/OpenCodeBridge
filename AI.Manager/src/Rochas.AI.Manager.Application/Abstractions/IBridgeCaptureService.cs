using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Application.Abstractions;

public interface IBridgeCaptureService
{
    Task<int> RecordAsync(string mode, string? requestJson, string? responseJson, string? requestHash, CancellationToken ct = default);
    Task<BridgeCapture?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<BridgeCapture>> ListAsync(CancellationToken ct = default);
}
