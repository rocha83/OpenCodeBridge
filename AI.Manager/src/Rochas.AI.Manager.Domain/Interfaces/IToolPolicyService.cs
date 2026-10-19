using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IToolPolicyService
{
    Task<int> CreateAsync(string role, string allow, string deny, bool requireCall, int maxTurns, int stallLimit, CancellationToken ct = default);
    Task<ToolPolicy?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<ToolPolicy>> ListAsync(CancellationToken ct = default);
    Task UpdateAsync(int id, string allow, string deny, bool requireCall, int maxTurns, int stallLimit, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}
