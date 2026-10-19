using Microsoft.Data.Sqlite;
using System.Linq;
using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Harness;

namespace Rochas.AI.Manager.Application.Services;

public sealed class HarnessProfileService(
    IGenericRepository<HarnessProfile> profiles,
    IPersistenceRepository<HarnessProfile> profilesWrite,
    IGenericRepository<HarnessProfileVersion> versions,
    IPersistenceRepository<HarnessProfileVersion> versionsWrite,
    string connectionString) : IHarnessProfileService
{
    private readonly IGenericRepository<HarnessProfile> _profiles = profiles;
    private readonly IPersistenceRepository<HarnessProfile> _profilesWrite = profilesWrite;
    private readonly IGenericRepository<HarnessProfileVersion> _versions = versions;
    private readonly IPersistenceRepository<HarnessProfileVersion> _versionsWrite = versionsWrite;
    private readonly string _conn = connectionString;

    private async Task<HarnessProfile> LoadWithVersionsAsync(HarnessProfile p)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(_conn);
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM harness_profile_versions WHERE profile_id=@pid";
        cmd.Parameters.AddWithValue("@pid", p.Id);
        var vs = new List<HarnessProfileVersion>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            vs.Add(new HarnessProfileVersion
            {
                Id = r.GetInt32(r.GetOrdinal("id")),
                ProfileId = r.GetInt32(r.GetOrdinal("profile_id")),
                Version = r.GetInt32(r.GetOrdinal("version")),
                Mode = r.GetString(r.GetOrdinal("mode")),
                Checksum = r.IsDBNull(r.GetOrdinal("checksum")) ? null : r.GetString(r.GetOrdinal("checksum")),
                ApprovedBy = r.IsDBNull(r.GetOrdinal("approved_by")) ? null : r.GetString(r.GetOrdinal("approved_by")),
                ApprovedAt = r.IsDBNull(r.GetOrdinal("approved_at")) ? null : DateTime.Parse(r.GetString(r.GetOrdinal("approved_at"))),
                CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))),
                Name = r.GetString(r.GetOrdinal("name")),
                IsActive = r.GetInt32(r.GetOrdinal("is_active")) == 1
            });
        }
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
        var ps = await _profiles.QueryRaw("SELECT * FROM harness_profiles WHERE id = @id", new Dictionary<string, object> { ["@id"] = id });
        var p = ps.FirstOrDefault();
        if (p is null) return null;
        return await LoadWithVersionsAsync(p);
    }

    public async Task<IReadOnlyList<HarnessProfile>> ListAsync(CancellationToken ct = default)
    {
        var list = await _profiles.QueryRaw("SELECT * FROM harness_profiles", new Dictionary<string, object>());
        foreach (var p in list) await LoadWithVersionsAsync(p);
        return list.OrderBy(p => p.CreatedAt).ToList();
    }

    public async Task<int> AddVersionAsync(int profileId, string mode, string? checksum, CancellationToken ct = default)
    {
        var ps = await _profiles.QueryRaw("SELECT * FROM harness_profiles WHERE id = @id", new Dictionary<string, object> { ["@id"] = profileId });
        var p = ps.FirstOrDefault();
        if (p is null) throw new KeyNotFoundException($"Harness profile {profileId} not found.");
        var versions = await _versions.QueryRaw("SELECT * FROM harness_profile_versions WHERE profile_id = @profile_id", new Dictionary<string, object> { ["@profile_id"] = profileId });
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
        var vs = await _versions.QueryRaw("SELECT * FROM harness_profile_versions WHERE id = @id", new Dictionary<string, object> { ["@id"] = versionId });
        var v = vs.FirstOrDefault();
        if (v is null) throw new KeyNotFoundException($"Harness profile version {versionId} not found.");
        v.ApprovedBy = string.IsNullOrWhiteSpace(approvedBy) ? "unknown" : approvedBy.Trim();
        v.ApprovedAt = DateTime.UtcNow;
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(_conn);
        await c.OpenAsync();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE harness_profile_versions SET approved_by=@by, approved_at=@at WHERE id=@id";
        cmd.Parameters.AddWithValue("@by", (object?)v.ApprovedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", (object?)v.ApprovedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", versionId);
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) throw new InvalidOperationException($"Update affected 0 rows for {versionId}.");
        // verify
        using var c2 = new Microsoft.Data.Sqlite.SqliteConnection(_conn);
        await c2.OpenAsync();
        using var cmd2 = c2.CreateCommand();
        cmd2.CommandText = "SELECT approved_by FROM harness_profile_versions WHERE id=@id";
        cmd2.Parameters.AddWithValue("@id", versionId);
        var val = await cmd2.ExecuteScalarAsync();
        if (val is null || val == DBNull.Value) throw new InvalidOperationException($"Verify failed: approved_by null for {versionId}. rows={rows}");
    }
}
