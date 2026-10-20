using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Application.Services;

public sealed class HarnessAttachmentService(
    IGenericRepository<HarnessAttachment> repo,
    IPersistenceRepository<HarnessAttachment> write) : IHarnessAttachmentService
{
    private readonly IGenericRepository<HarnessAttachment> _repo = repo;
    private readonly IPersistenceRepository<HarnessAttachment> _write = write;

    public async Task<int> CreateAsync(int versionId, string kind, string role, string content, CancellationToken ct = default)
    {
        var a = new HarnessAttachment
        {
            VersionId = versionId,
            Kind = string.IsNullOrWhiteSpace(kind) ? "Core" : kind.Trim(),
            Role = string.IsNullOrWhiteSpace(role) ? "general" : role.Trim(),
            Content = content ?? string.Empty,
            CharCount = (content ?? string.Empty).Length
        };
        await _write.Add(a);
        return a.Id;
    }

    public async Task<IReadOnlyList<HarnessAttachment>> ListByVersionAsync(int versionId, CancellationToken ct = default)
    {
        var list = await _repo.Query(new HarnessAttachment { VersionId = versionId });
        return list.OrderBy(a => a.Id).ToList();
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _write.Remove(new HarnessAttachment { Id = id });
    }
}
