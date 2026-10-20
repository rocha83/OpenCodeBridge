using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Application.Services;

public sealed class HarnessRunService(
    IGenericRepository<HarnessRun> repo,
    IPersistenceRepository<HarnessRun> write) : IHarnessRunService
{
    private readonly IGenericRepository<HarnessRun> _repo = repo;
    private readonly IPersistenceRepository<HarnessRun> _write = write;

    public async Task<int> RecordAsync(int evidenceId, int profileVersionId, int modelBindingId, string? requestHash, int toolCalls, int filesCreated, int resultCode, long durationMs, double tokensPerSecond, CancellationToken ct = default)
    {
        var r = new HarnessRun
        {
            EvidenceId = evidenceId,
            ProfileVersionId = profileVersionId,
            ModelBindingId = modelBindingId,
            RequestHash = requestHash,
            ToolCalls = toolCalls,
            FilesCreated = filesCreated,
            ResultCode = resultCode,
            DurationMs = durationMs,
            TokensPerSecond = tokensPerSecond
        };
        await _write.Add(r);
        return r.Id;
    }

    public async Task<HarnessRun?> GetAsync(int id, CancellationToken ct = default) { var list = await _repo.Query(new HarnessRun { Id = id }); return list.FirstOrDefault(); }

    public async Task<IReadOnlyList<HarnessRun>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new HarnessRun());
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<HarnessRun>> ListByProfileVersionAsync(int profileVersionId, CancellationToken ct = default)
    {
        var list = await _repo.Query(new HarnessRun { ProfileVersionId = profileVersionId });
        return list.OrderByDescending(r => r.CreatedAt).ToList();
    }
}
