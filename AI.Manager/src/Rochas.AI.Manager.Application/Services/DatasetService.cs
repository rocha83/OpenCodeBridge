using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Datasets;
using System.IO;
using System.Text.Json;

namespace Rochas.AI.Manager.Application.Services;

public sealed class DatasetService(
    IGenericRepository<Dataset> repo,
    IPersistenceRepository<Dataset> write) : IDatasetService
{
    private readonly IGenericRepository<Dataset> _repo = repo;
    private readonly IPersistenceRepository<Dataset> _write = write;

    private static readonly HashSet<string> ValidFormats = new(StringComparer.OrdinalIgnoreCase) { "jsonl", "csv", "parquet" };

    public async Task<int> CreateAsync(string name, string format, string? version, string? filePath, string? fileHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        if (!ValidFormats.Contains(format)) throw new ArgumentException("Unsupported dataset format.", nameof(format));

        var d = new Dataset
        {
            Name = name.Trim(),
            Format = format.Trim().ToLowerInvariant(),
            Version = version?.Trim(),
            FilePath = filePath?.Trim(),
            FileHash = fileHash?.Trim()
        };
        await _write.Add(d);
        return d.Id;
    }

    public async Task<Dataset?> GetAsync(int id, CancellationToken ct = default)
    {
        var list = await _repo.Query(new Dataset { Id = id });
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<Dataset>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new Dataset());
        return list.OrderBy(d => d.CreatedAt).ToList();
    }

    public async Task ValidateAsync(int id, bool isValid, CancellationToken ct = default)
    {
        var d = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Dataset {id} not found.");
        var old = new Dataset(); foreach (var p in typeof(Dataset).GetProperties()) p.SetValue(old, p.GetValue(d));
        d.IsValidated = isValid;
        await _write.Update(d, old);
    }

    public async Task<bool> ValidateFormatAsync(int id, string mode, CancellationToken ct = default)
    {
        var d = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Dataset {id} not found.");
        if (string.IsNullOrWhiteSpace(d.FilePath) || !File.Exists(d.FilePath)) return false;
        mode = (mode ?? "sft").ToLowerInvariant();
        foreach (var raw in await File.ReadAllLinesAsync(d.FilePath, ct))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (mode == "sft")
            {
                if (!root.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array) return false;
            }
            else
            {
                if (!root.TryGetProperty("prompt", out _) || !root.TryGetProperty("chosen", out _) || !root.TryGetProperty("rejected", out _)) return false;
            }
        }
        return true;
    }
}
