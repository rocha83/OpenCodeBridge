using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Application.Services;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Annotations;
using Rochas.Data.Specification.Enums;
using Rochas.AI.Manager.Infrastructure.Data;

namespace Rochas.AI.Manager.Tests;

public class HarnessProfileServiceTests
{
    private static HarnessProfileService CreateService(string dbPath)
    {
        AppDb.Init(dbPath);
        var conn = AppDb.ConnectionString;
        var profiles = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfile>(DatabaseEngine.SQLite, conn);
        var versions = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfileVersion>(DatabaseEngine.SQLite, conn);
        return new HarnessProfileService(profiles, profiles, versions, versions);
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
