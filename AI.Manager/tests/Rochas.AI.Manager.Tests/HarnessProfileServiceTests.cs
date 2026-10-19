using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Application.Services;

namespace Rochas.AI.Manager.Tests;

public class HarnessProfileServiceTests
{
    [Fact]
    public async Task Create_List_AddVersion_Approve_Works()
    {
        IHarnessProfileService svc = new InMemoryHarnessProfileService();
        var id = await svc.CreateAsync("Profile A", "desc");
        var list = await svc.ListAsync();
        Assert.Single(list);
        var vid = await svc.AddVersionAsync(id, "Hybrid", "abc");
        var profile = await svc.GetAsync(id);
        Assert.NotNull(profile);
        Assert.Single(profile!.Versions);
        await svc.ApproveVersionAsync(vid, "admin");
        profile = await svc.GetAsync(id);
        Assert.NotNull(profile!.Versions[0].ApprovedAt);
    }

    [Fact]
    public async Task Linter_Finds_Hardcodes()
    {
        IHarnessLinter linter = new HarnessLinter();
        var findings = linter.FindHardcodes("model gemma on :4124 with 64k context temp:0.5 path /media");
        Assert.NotEmpty(findings);
    }

    [Fact]
    public async Task Linter_Accepts_Clean_Text()
    {
        IHarnessLinter linter = new HarnessLinter();
        var findings = linter.FindHardcodes("Cumpra o enunciado antes de responder; valide scripts com bash -n.");
        Assert.Empty(findings);
    }
}
