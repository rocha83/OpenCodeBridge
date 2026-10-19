using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Application.Services;

public sealed class ToolPolicyService(
    IGenericRepository<ToolPolicy> repo,
    IPersistenceRepository<ToolPolicy> write) : IToolPolicyService
{
    private readonly IGenericRepository<ToolPolicy> _repo = repo;
    private readonly IPersistenceRepository<ToolPolicy> _write = write;

    public async Task<int> CreateAsync(string role, string allow, string deny, bool requireCall, int maxTurns, int stallLimit, CancellationToken ct = default)
    {
        var p = new ToolPolicy
        {
            Role = role,
            Allow = allow ?? string.Empty,
            Deny = deny ?? string.Empty,
            RequireCall = requireCall,
            MaxTurns = maxTurns <= 0 ? 10 : maxTurns,
            StallLimit = stallLimit <= 0 ? 3 : stallLimit
        };
        await _write.Add(p);
        return p.Id;
    }

    public async Task<ToolPolicy?> GetAsync(int id, CancellationToken ct = default) => (await _repo.QueryRaw("SELECT * FROM tool_policies WHERE id=@id", new Dictionary<string, object> { ["@id"] = id })).FirstOrDefault();

    public async Task<IReadOnlyList<ToolPolicy>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.QueryRaw("SELECT * FROM tool_policies", new Dictionary<string, object>());
        return list.OrderBy(p => p.Role).ToList();
    }

    public async Task UpdateAsync(int id, string allow, string deny, bool requireCall, int maxTurns, int stallLimit, CancellationToken ct = default)
    {
        var p = (await _repo.QueryRaw("SELECT * FROM tool_policies WHERE id=@id", new Dictionary<string, object> { ["@id"] = id })).FirstOrDefault() ?? throw new KeyNotFoundException($"ToolPolicy {id} not found.");
        p.Allow = allow ?? string.Empty;
        p.Deny = deny ?? string.Empty;
        p.RequireCall = requireCall;
        p.MaxTurns = maxTurns <= 0 ? p.MaxTurns : maxTurns;
        p.StallLimit = stallLimit <= 0 ? p.StallLimit : stallLimit;
        await _write.Update(p, new ToolPolicy { Id = id });
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _write.Remove(new ToolPolicy { Id = id });
    }
}
