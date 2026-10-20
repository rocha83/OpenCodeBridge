using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Application.Services;

public sealed class HarnessProfileService(
    IGenericRepository<HarnessProfile> profiles,
    IPersistenceRepository<HarnessProfile> profilesWrite,
    IGenericRepository<HarnessProfileVersion> versions,
    IPersistenceRepository<HarnessProfileVersion> versionsWrite) : IHarnessProfileService
{
    private readonly IGenericRepository<HarnessProfile> _profiles = profiles;
    private readonly IPersistenceRepository<HarnessProfile> _profilesWrite = profilesWrite;
    private readonly IGenericRepository<HarnessProfileVersion> _versions = versions;
    private readonly IPersistenceRepository<HarnessProfileVersion> _versionsWrite = versionsWrite;

    private async Task<HarnessProfile> LoadWithVersionsAsync(HarnessProfile p)
    {
        var vs = await _versions.Query(new HarnessProfileVersion { ProfileId = p.Id });
        p.Versions = vs.OrderByDescending(v => v.Version).ToList();
        return p;
    }

    public async Task<int> CreateAsync(string name, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        var p = new HarnessProfile { Name = name.Trim(), Description = description?.Trim() };
        await _profilesWrite.Add(p);
        return p.Id;
    }

    public async Task<HarnessProfile?> GetAsync(int id, CancellationToken ct = default)
    {
        var list = await _profiles.Query(new HarnessProfile { Id = id });
        var p = list.FirstOrDefault();
        if (p is null) return null;
        return await LoadWithVersionsAsync(p);
    }

    public async Task<IReadOnlyList<HarnessProfile>> ListAsync(CancellationToken ct = default)
    {
        var list = await _profiles.Query(new HarnessProfile());
        foreach (var p in list) await LoadWithVersionsAsync(p);
        return list.OrderBy(p => p.CreatedAt).ToList();
    }

    public async Task<int> AddVersionAsync(int profileId, string mode, string? checksum, CancellationToken ct = default)
    {
        var ps = await _profiles.Query(new HarnessProfile { Id = profileId });
        var p = ps.FirstOrDefault();
        if (p is null) throw new KeyNotFoundException($"Harness profile {profileId} not found.");
        var versions = await _versions.Query(new HarnessProfileVersion { ProfileId = profileId });
        var v = new HarnessProfileVersion
        {
            ProfileId = profileId,
            Version = versions.Count + 1,
            Mode = string.IsNullOrWhiteSpace(mode) ? "ClientNative" : mode.Trim(),
            Checksum = checksum
        };
        await _versionsWrite.Add(v);
        return v.Id;
    }

    public async Task ApproveVersionAsync(int versionId, string approvedBy, CancellationToken ct = default)
    {
        var vs = await _versions.Query(new HarnessProfileVersion { Id = versionId });
        var v = vs.FirstOrDefault();
        if (v is null) throw new KeyNotFoundException($"Harness profile version {versionId} not found.");
        var old = new HarnessProfileVersion(); foreach (var prop in typeof(HarnessProfileVersion).GetProperties()) prop.SetValue(old, prop.GetValue(v));
        v.ApprovedBy = string.IsNullOrWhiteSpace(approvedBy) ? "unknown" : approvedBy.Trim();
        v.ApprovedAt = DateTime.UtcNow;
        await _versionsWrite.Update(v, old);
    }
}
