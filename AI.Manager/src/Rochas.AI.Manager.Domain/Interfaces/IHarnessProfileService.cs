namespace Rochas.AI.Manager.Domain.Interfaces;

using Rochas.AI.Manager.Domain.Harness;

public interface IHarnessProfileService
{
    Task<int> CreateAsync(string name, string? description, CancellationToken ct = default);
    Task<HarnessProfile?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<HarnessProfile>> ListAsync(CancellationToken ct = default);
    Task<int> AddVersionAsync(int profileId, string mode, string? checksum, CancellationToken ct = default);
    Task ApproveVersionAsync(int versionId, string approvedBy, CancellationToken ct = default);
}
