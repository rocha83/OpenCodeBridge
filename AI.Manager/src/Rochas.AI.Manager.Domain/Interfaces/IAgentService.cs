using Rochas.AI.Manager.Domain.Entities;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IAgentService
{
    Task<int> CreateAsync(string name, string model, string bridgeUrl, double temperature, string thinking, string mode, string tools, string systemPrompt, CancellationToken ct = default);
    Task<Agent?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct = default);
    Task UpdateAsync(int id, string name, string model, string bridgeUrl, double temperature, string thinking, string mode, string tools, string systemPrompt, bool active, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}
