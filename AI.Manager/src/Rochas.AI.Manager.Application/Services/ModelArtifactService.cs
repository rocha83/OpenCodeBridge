using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Models;

namespace Rochas.AI.Manager.Application.Services;

public sealed class ModelArtifactService(
    IGenericRepository<ModelArtifact> repo,
    IPersistenceRepository<ModelArtifact> write) : IModelArtifactService
{
    private readonly IGenericRepository<ModelArtifact> _repo = repo;
    private readonly IPersistenceRepository<ModelArtifact> _write = write;

    private static readonly HashSet<string> ValidFamilies = new(StringComparer.OrdinalIgnoreCase) { "Gemma4", "Llama", "Qwen", "DeepSeek" };
    private static readonly HashSet<string> ValidFormats = new(StringComparer.OrdinalIgnoreCase) { "safetensors", "gguf", "litertlm" };

    public async Task<int> CreateAsync(string name, string family, string? variant, string format, string? filePath, bool isTrainable, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        if (!ValidFamilies.Contains(family)) throw new ArgumentException("Unsupported model family.", nameof(family));
        if (!ValidFormats.Contains(format)) throw new ArgumentException("Unsupported format.", nameof(format));

        var a = new ModelArtifact
        {
            Name = name.Trim(),
            Family = family.Trim(),
            Variant = variant?.Trim(),
            Format = format.Trim().ToLowerInvariant(),
            FilePath = filePath?.Trim(),
            IsTrainable = isTrainable
        };
        await _write.Add(a);
        return a.Id;
    }

    public async Task<ModelArtifact?> GetAsync(int id, CancellationToken ct = default) { var list = await _repo.Query(new ModelArtifact { Id = id }); return list.FirstOrDefault(); }

    public async Task<IReadOnlyList<ModelArtifact>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new ModelArtifact());
        return list.OrderBy(a => a.CreatedAt).ToList();
    }

    public async Task ApproveAsync(int id, CancellationToken ct = default)
    {
        var a = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Model artifact {id} not found.");
        if (a.Status != "Draft") throw new InvalidOperationException("Only Draft artifacts can be approved.");
        var old = new ModelArtifact();
        foreach (var prop in typeof(ModelArtifact).GetProperties()) prop.SetValue(old, prop.GetValue(a));
        a.Status = "Approved";
        await _write.Update(a, old);
    }

    public async Task DeprecateAsync(int id, CancellationToken ct = default)
    {
        var a = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Model artifact {id} not found.");
        var old = new ModelArtifact();
        foreach (var prop in typeof(ModelArtifact).GetProperties()) prop.SetValue(old, prop.GetValue(a));
        a.Status = "Deprecated";
        await _write.Update(a, old);
    }

    public async Task UpdateHashAsync(int id, string hash, CancellationToken ct = default)
    {
        var a = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Model artifact {id} not found.");
        var old = new ModelArtifact();
        foreach (var prop in typeof(ModelArtifact).GetProperties()) prop.SetValue(old, prop.GetValue(a));
        a.FileHash = hash;
        await _write.Update(a, old);
    }
}
