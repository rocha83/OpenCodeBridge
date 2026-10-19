namespace Rochas.AI.Manager.Application.Abstractions;

using Rochas.AI.Manager.Domain.Harness;

public interface IHarnessProfileService
{
    Task<Guid> CreateAsync(string name, string? description, CancellationToken ct = default);
    Task<HarnessProfile?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<HarnessProfile>> ListAsync(CancellationToken ct = default);
    Task<Guid> AddVersionAsync(Guid profileId, string mode, string? checksum, CancellationToken ct = default);
    Task ApproveVersionAsync(Guid versionId, string approvedBy, CancellationToken ct = default);
}
