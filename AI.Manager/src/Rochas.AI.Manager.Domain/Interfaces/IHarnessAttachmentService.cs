using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IHarnessAttachmentService
{
    Task<int> CreateAsync(int versionId, string kind, string role, string content, CancellationToken ct = default);
    Task<IReadOnlyList<HarnessAttachment>> ListByVersionAsync(int versionId, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}
