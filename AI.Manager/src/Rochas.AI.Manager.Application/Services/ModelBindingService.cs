using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Application.Services;

public sealed class ModelBindingService(
    IGenericRepository<ModelBinding> repo,
    IPersistenceRepository<ModelBinding> write) : IModelBindingService
{
    private readonly IGenericRepository<ModelBinding> _repo = repo;
    private readonly IPersistenceRepository<ModelBinding> _write = write;

    public async Task<int> CreateAsync(string provider, string modelId, int maxTokens, int contextWindow, string thinking, double temperature, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider is required.", nameof(provider));
        if (string.IsNullOrWhiteSpace(modelId)) throw new ArgumentException("ModelId is required.", nameof(modelId));
        var b = new ModelBinding
        {
            Provider = provider.Trim(),
            ModelId = modelId.Trim(),
            MaxTokens = maxTokens <= 0 ? 4096 : maxTokens,
            ContextWindow = contextWindow <= 0 ? 32768 : contextWindow,
            Thinking = string.IsNullOrWhiteSpace(thinking) ? "off" : thinking.Trim(),
            Temperature = temperature < 0 ? 0.5 : temperature
        };
        await _write.Add(b);
        return b.Id;
    }

    public async Task<ModelBinding?> GetAsync(int id, CancellationToken ct = default) => await _repo.Get(id);

    public async Task<IReadOnlyList<ModelBinding>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.QueryRaw("SELECT * FROM model_bindings", new Dictionary<string, object>());
        return list.OrderBy(b => b.Provider).ThenBy(b => b.ModelId).ToList();
    }

    public async Task UpdateAsync(int id, int maxTokens, int contextWindow, string thinking, double temperature, CancellationToken ct = default)
    {
        var b = await _repo.Get(id) ?? throw new KeyNotFoundException($"ModelBinding {id} not found.");
        b.MaxTokens = maxTokens <= 0 ? b.MaxTokens : maxTokens;
        b.ContextWindow = contextWindow <= 0 ? b.ContextWindow : contextWindow;
        b.Thinking = string.IsNullOrWhiteSpace(thinking) ? b.Thinking : thinking.Trim();
        b.Temperature = temperature < 0 ? b.Temperature : temperature;
        await _write.Update(b, new ModelBinding { Id = id });
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _write.Remove(new ModelBinding { Id = id });
    }
}
