using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IModelBindingService
{
    Task<int> CreateAsync(string provider, string modelId, int maxTokens, int contextWindow, string thinking, double temperature, CancellationToken ct = default);
    Task<ModelBinding?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<ModelBinding>> ListAsync(CancellationToken ct = default);
    Task UpdateAsync(int id, int maxTokens, int contextWindow, string thinking, double temperature, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}
