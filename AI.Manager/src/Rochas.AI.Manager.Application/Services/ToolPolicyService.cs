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

    public async Task<ToolPolicy?> GetAsync(int id, CancellationToken ct = default) { var list = await _repo.Query(new ToolPolicy { Id = id }); return list.FirstOrDefault(); }

    public async Task<IReadOnlyList<ToolPolicy>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new ToolPolicy());
        return list.OrderBy(p => p.Role).ToList();
    }

    public async Task UpdateAsync(int id, string allow, string deny, bool requireCall, int maxTurns, int stallLimit, CancellationToken ct = default)
    {
        var p = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"ToolPolicy {id} not found.");
        var old = new ToolPolicy(); foreach (var prop in typeof(ToolPolicy).GetProperties()) prop.SetValue(old, prop.GetValue(p));
        p.Allow = allow ?? string.Empty;
        p.Deny = deny ?? string.Empty;
        p.RequireCall = requireCall;
        p.MaxTurns = maxTurns <= 0 ? p.MaxTurns : maxTurns;
        p.StallLimit = stallLimit <= 0 ? p.StallLimit : stallLimit;
        await _write.Update(p, old);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _write.Remove(new ToolPolicy { Id = id });
    }
}
