using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Application.Services;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Annotations;
using Rochas.Data.Specification.Enums;

namespace Rochas.AI.Manager.Tests;

public class HarnessProfileServiceTests
{
    private static HarnessProfileService CreateService(string dbPath)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS harness_profiles (id INTEGER PRIMARY KEY AUTOINCREMENT, parent_id INTEGER, code TEXT, name TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1, description TEXT, status TEXT NOT NULL DEFAULT 'Draft', created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS harness_profile_versions (id INTEGER PRIMARY KEY AUTOINCREMENT, parent_id INTEGER, code TEXT, name TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1, profile_id INTEGER NOT NULL, version INTEGER NOT NULL, mode TEXT NOT NULL DEFAULT 'ClientNative', checksum TEXT, approved_by TEXT, approved_at TEXT, created_at TEXT NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        var conn = $"Data Source={dbPath};Cache=Shared";
        var profiles = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfile>(DatabaseEngine.SQLite, conn);
        var versions = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfileVersion>(DatabaseEngine.SQLite, conn);
        return new HarnessProfileService(profiles, profiles, versions, versions, $"Data Source={dbPath}");
    }

    [Fact]
    public async Task Create_List_AddVersion_Approve_Works()
    {
        var db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hp_{Guid.NewGuid()}.db");
        var svc = CreateService(db);
        var id = await svc.CreateAsync("Profile A", "desc");
        var list = await svc.ListAsync();
        Assert.Single(list);
        var vid = await svc.AddVersionAsync(id, "Hybrid", "abc");
        var profile = await svc.GetAsync(id);
        Assert.NotNull(profile);
        Assert.Single(profile!.Versions);

        await svc.ApproveVersionAsync(vid, "admin");
        profile = await svc.GetAsync(id);
        Assert.NotNull(profile);
        Assert.Equal("admin", profile!.Versions[0].ApprovedBy);
        Assert.NotNull(profile!.Versions[0].ApprovedAt);

        System.IO.File.Delete(db);
    }

    [Fact]
    public void Linter_Finds_Hardcodes()
    {
        var linter = new HarnessLinter();
        var findings = linter.FindHardcodes("model gemma on :4124 with 64k context temp:0.5 path /media");
        Assert.NotEmpty(findings);
    }

    [Fact]
    public void Linter_Accepts_Clean_Text()
    {
        var linter = new HarnessLinter();
        var findings = linter.FindHardcodes("Cumpra o enunciado antes de responder; valide scripts com bash -n.");
        Assert.Empty(findings);
    }
}
