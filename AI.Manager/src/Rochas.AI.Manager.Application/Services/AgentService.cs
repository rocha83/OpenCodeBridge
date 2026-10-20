using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Entities;

namespace Rochas.AI.Manager.Application.Services;

public sealed class AgentService(
    IGenericRepository<Agent> repo,
    IPersistenceRepository<Agent> write) : IAgentService
{
    private readonly IGenericRepository<Agent> _repo = repo;
    private readonly IPersistenceRepository<Agent> _write = write;

    public async Task<int> CreateAsync(string name, string model, string bridgeUrl, double temperature, string thinking, string mode, string tools, string systemPrompt, CancellationToken ct = default)
    {
        var a = new Agent { Name = name, Model = model, BridgeUrl = bridgeUrl, Temperature = temperature, Thinking = thinking, Mode = mode, Tools = tools, SystemPrompt = systemPrompt, IsActive = true };
        await _write.Add(a);
        return a.Id;
    }

    public async Task<Agent?> GetAsync(int id, CancellationToken ct = default)
    {
        var list = await _repo.Query(new Agent { Id = id });
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new Agent());
        return list.OrderBy(a => a.Name).ToList();
    }

    public async Task UpdateAsync(int id, string name, string model, string bridgeUrl, double temperature, string thinking, string mode, string tools, string systemPrompt, bool active, CancellationToken ct = default)
    {
        var a = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Agent {id} not found.");
        var old = new Agent(); foreach (var p in typeof(Agent).GetProperties()) p.SetValue(old, p.GetValue(a));
        a.Name = name;
        a.Model = model;
        a.BridgeUrl = bridgeUrl;
        a.Temperature = temperature;
        a.Thinking = thinking;
        a.Mode = mode;
        a.Tools = tools;
        a.SystemPrompt = systemPrompt;
        a.IsActive = active;
        await _write.Update(a, old);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _write.Remove(new Agent { Id = id });
    }
}
