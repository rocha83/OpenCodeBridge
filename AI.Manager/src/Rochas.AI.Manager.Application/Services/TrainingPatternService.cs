using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Profiles;

namespace Rochas.AI.Manager.Application.Services;

public sealed class TrainingPatternService(
    IGenericRepository<TrainingPattern> repo,
    IPersistenceRepository<TrainingPattern> write) : ITrainingPatternService
{
    private readonly IGenericRepository<TrainingPattern> _repo = repo;
    private readonly IPersistenceRepository<TrainingPattern> _write = write;

    public async Task<int> CreateAsync(string name, int? profileId, int? modelArtifactId, string? patternJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        var p = new TrainingPattern
        {
            Name = name.Trim(),
            ProfileId = profileId,
            ModelArtifactId = modelArtifactId,
            PatternJson = patternJson
        };
        await _write.Add(p);
        return p.Id;
    }

    public async Task<TrainingPattern?> GetAsync(int id, CancellationToken ct = default) => await _repo.Get(new TrainingPattern { Id = id });

    public async Task<IReadOnlyList<TrainingPattern>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new TrainingPattern());
        return list.OrderBy(p => p.CreatedAt).ToList();
    }
}
