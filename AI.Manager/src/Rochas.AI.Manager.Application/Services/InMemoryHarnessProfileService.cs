namespace Rochas.AI.Manager.Application.Services;

using System.Collections.Concurrent;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Harness;

/// <summary>
/// Armazenamento em memoria para perfis de harness (vertical slice inicial).
/// Persistencia EF Core sera adicionada em fase posterior.
/// </summary>
public sealed class InMemoryHarnessProfileService : IHarnessProfileService
{
    private readonly ConcurrentDictionary<Guid, HarnessProfile> _profiles = new();

    public Task<Guid> CreateAsync(string name, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        var profile = new HarnessProfile { Name = name.Trim(), Description = description?.Trim() };
        _profiles[profile.Id] = profile;
        return Task.FromResult(profile.Id);
    }

    public Task<HarnessProfile?> GetAsync(Guid id, CancellationToken ct = default)
    {
        _profiles.TryGetValue(id, out var profile);
        return Task.FromResult(profile);
    }

    public Task<IReadOnlyList<HarnessProfile>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<HarnessProfile> list = _profiles.Values
            .OrderBy(p => p.CreatedAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<Guid> AddVersionAsync(Guid profileId, string mode, string? checksum, CancellationToken ct = default)
    {
        if (!_profiles.TryGetValue(profileId, out var profile))
            throw new KeyNotFoundException($"Harness profile {profileId} not found.");

        var version = new HarnessProfileVersion
        {
            ProfileId = profileId,
            Version = profile.Versions.Count + 1,
            Mode = string.IsNullOrWhiteSpace(mode) ? "ClientNative" : mode.Trim(),
            Checksum = checksum
        };
        profile.Versions.Add(version);
        return Task.FromResult(version.Id);
    }

    public Task ApproveVersionAsync(Guid versionId, string approvedBy, CancellationToken ct = default)
    {
        var version = _profiles.Values
            .SelectMany(p => p.Versions)
            .FirstOrDefault(v => v.Id == versionId);

        if (version is null)
            throw new KeyNotFoundException($"Harness profile version {versionId} not found.");

        version.ApprovedBy = string.IsNullOrWhiteSpace(approvedBy) ? "unknown" : approvedBy.Trim();
        version.ApprovedAt = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}
