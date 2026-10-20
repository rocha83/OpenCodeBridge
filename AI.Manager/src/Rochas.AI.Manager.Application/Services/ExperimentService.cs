using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Experiments;

namespace Rochas.AI.Manager.Application.Services;

public sealed class ExperimentService(
    IGenericRepository<Experiment> repo,
    IPersistenceRepository<Experiment> write) : IExperimentService
{
    private readonly IGenericRepository<Experiment> _repo = repo;
    private readonly IPersistenceRepository<Experiment> _write = write;

    public async Task<int> CreateAsync(string name, string? hypothesis, int? baseArtifactId, int? tunedArtifactId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        var e = new Experiment
        {
            Name = name.Trim(),
            Hypothesis = hypothesis?.Trim(),
            BaseArtifactId = baseArtifactId,
            TunedArtifactId = tunedArtifactId
        };
        await _write.Add(e);
        return e.Id;
    }

    public async Task<Experiment?> GetAsync(int id, CancellationToken ct = default)
    {
        var list = await _repo.Query(new Experiment { Id = id });
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<Experiment>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.Query(new Experiment());
        return list.OrderByDescending(e => e.CreatedAt).ToList();
    }

    public async Task StartAsync(int id, CancellationToken ct = default)
    {
        var e = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Experiment {id} not found.");
        if (e.Status != "Planned") throw new InvalidOperationException("Only Planned experiments can start.");
        var old = new Experiment(); foreach (var p in typeof(Experiment).GetProperties()) p.SetValue(old, p.GetValue(e));
        e.Status = "Running";
        await _write.Update(e, old);
    }

    public async Task CompleteAsync(int id, string? metricsJson, CancellationToken ct = default)
    {
        var e = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Experiment {id} not found.");
        if (e.Status != "Running") throw new InvalidOperationException("Only Running experiments can complete.");
        var old = new Experiment(); foreach (var p in typeof(Experiment).GetProperties()) p.SetValue(old, p.GetValue(e));
        e.Status = "Completed";
        e.MetricsJson = metricsJson;
        await _write.Update(e, old);
    }

    public async Task ApproveAsync(int id, CancellationToken ct = default)
    {
        var e = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Experiment {id} not found.");
        if (e.Status != "Completed") throw new InvalidOperationException("Only Completed experiments can be approved.");
        var old = new Experiment(); foreach (var p in typeof(Experiment).GetProperties()) p.SetValue(old, p.GetValue(e));
        e.Status = "Approved";
        await _write.Update(e, old);
    }

    public async Task RejectAsync(int id, CancellationToken ct = default)
    {
        var e = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"Experiment {id} not found.");
        var old = new Experiment(); foreach (var p in typeof(Experiment).GetProperties()) p.SetValue(old, p.GetValue(e));
        e.Status = "Rejected";
        await _write.Update(e, old);
    }
}
