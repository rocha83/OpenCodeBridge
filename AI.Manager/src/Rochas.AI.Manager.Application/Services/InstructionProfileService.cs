using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Profiles;

namespace Rochas.AI.Manager.Application.Services;

public sealed class InstructionProfileService(
    IGenericRepository<InstructionProfile> repo,
    IPersistenceRepository<InstructionProfile> write) : IInstructionProfileService
{
    private readonly IGenericRepository<InstructionProfile> _repo = repo;
    private readonly IPersistenceRepository<InstructionProfile> _write = write;

    public async Task<int> CreateAsync(string name, string? description, string? instructionsJson, string? version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        var p = new InstructionProfile
        {
            Name = name.Trim(),
            Description = description?.Trim(),
            InstructionsJson = instructionsJson,
            Version = version?.Trim()
        };
        await _write.Add(p);
        return p.Id;
    }

    public async Task<InstructionProfile?> GetAsync(int id, CancellationToken ct = default) { var list = await _repo.Query(new InstructionProfile { Id = id }); return list.FirstOrDefault(); }

    public async Task<IReadOnlyList<InstructionProfile>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new InstructionProfile());
        return list.OrderBy(p => p.CreatedAt).ToList();
    }

    public async Task ApproveAsync(int id, CancellationToken ct = default)
    {
        var p = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Instruction profile {id} not found.");
        if (p.Status != "Draft") throw new InvalidOperationException("Only Draft profiles can be approved.");
        var old = new InstructionProfile(); foreach (var prop in typeof(InstructionProfile).GetProperties()) prop.SetValue(old, prop.GetValue(p));
        p.Status = "Approved";
        await _write.Update(p, old);
    }

    public async Task DeprecateAsync(int id, CancellationToken ct = default)
    {
        var p = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Instruction profile {id} not found.");
        var old = new InstructionProfile(); foreach (var prop in typeof(InstructionProfile).GetProperties()) prop.SetValue(old, prop.GetValue(p));
        p.Status = "Deprecated";
        await _write.Update(p, old);
    }
}
