using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Application.Services;

public sealed class BridgeCaptureService(
    IGenericRepository<BridgeCapture> repo,
    IPersistenceRepository<BridgeCapture> write) : IBridgeCaptureService
{
    private readonly IGenericRepository<BridgeCapture> _repo = repo;
    private readonly IPersistenceRepository<BridgeCapture> _write = write;

    public async Task<int> RecordAsync(string mode, string? requestJson, string? responseJson, string? requestHash, CancellationToken ct = default)
    {
        var c = new BridgeCapture
        {
            Mode = string.IsNullOrWhiteSpace(mode) ? "unknown" : mode.Trim(),
            RequestJson = requestJson,
            ResponseJson = responseJson,
            RequestHash = requestHash
        };
        await _write.Add(c);
        return c.Id;
    }

    public async Task<BridgeCapture?> GetAsync(int id, CancellationToken ct = default) { var list = await _repo.Query(new BridgeCapture { Id = id }); return list.FirstOrDefault(); }

    public async Task<IReadOnlyList<BridgeCapture>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new BridgeCapture());
        return list.OrderByDescending(c => c.CreatedAt).ToList();
    }
}
