using Rochas.AI.Manager.Domain.Models;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IModelArtifactService
{
    Task<int> CreateAsync(string name, string family, string? variant, string format, string? filePath, bool isTrainable, CancellationToken ct = default);
    Task<ModelArtifact?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<ModelArtifact>> ListAsync(CancellationToken ct = default);
    Task ApproveAsync(int id, CancellationToken ct = default);
    Task DeprecateAsync(int id, CancellationToken ct = default);
    Task UpdateHashAsync(int id, string hash, CancellationToken ct = default);
}
