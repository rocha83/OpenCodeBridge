using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Leitura dos prompt_controls com cache curto (calda aplicada em todo request).
// Escrita: FluxoController (admin). Comentarios pt-BR.
public sealed class PromptControlService(IGenericRepository<PromptControl> repo)
{
    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _at = DateTime.MinValue;

    public async Task<Dictionary<string, string>> AllAsync()
    {
        if ((DateTime.UtcNow - _at).TotalSeconds < 30 && _cache.Count > 0) return _cache;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pc in await repo.Query(new PromptControl()))
            if (!string.IsNullOrEmpty(pc.Key)) dict[pc.Key] = pc.Value ?? "";
        _cache = dict;
        _at = DateTime.UtcNow;
        return dict;
    }

    public async Task<string> GetAsync(string key)
    {
        var all = await AllAsync();
        return all.TryGetValue(key, out string? v) ? v : "";
    }

    public void Invalidate()
    {
        _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _at = DateTime.MinValue;
    }
}
