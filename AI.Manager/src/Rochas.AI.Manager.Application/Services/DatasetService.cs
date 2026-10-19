using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Datasets;

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

    public async Task<Dataset?> GetAsync(int id, CancellationToken ct = default) => await _repo.Get(id);

    public async Task<IReadOnlyList<Dataset>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.QueryRaw("SELECT * FROM datasets", new Dictionary<string, object>());
        return list.OrderBy(d => d.CreatedAt).ToList();
    }

    public async Task ValidateAsync(int id, bool isValid, CancellationToken ct = default)
    {
        var d = await _repo.Get(id) ?? throw new KeyNotFoundException($"Dataset {id} not found.");
        d.IsValidated = isValid;
        await _write.Update(d, new Dataset { Id = id });
    }
}
