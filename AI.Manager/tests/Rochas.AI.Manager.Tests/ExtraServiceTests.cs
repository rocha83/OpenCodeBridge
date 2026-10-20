using Rochas.AI.Manager.Application.Services;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Xunit;

namespace Rochas.AI.Manager.Tests;

[Collection("Sequential")]
public class ExtraServiceTests
{
    private static string NewDb() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ex_{Guid.NewGuid()}.db");
    private static void Init(string db) => AppDb.Init(db);

    [Fact]
    public async Task ModelArtifact_Approve_Deprecate_UpdateHash()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Models.ModelArtifact>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Models.ModelArtifact>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new ModelArtifactService(repo, write);
        var id = await svc.CreateAsync("ma", "Gemma4", "12b", "gguf", "/tmp/m.gguf", true);
        await svc.ApproveAsync(id);
        await svc.UpdateHashAsync(id, "newhash");
        await svc.DeprecateAsync(id);
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task InstructionProfile_Approve_Deprecate()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Profiles.InstructionProfile>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Profiles.InstructionProfile>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new InstructionProfileService(repo, write);
        var id = await svc.CreateAsync("ip", "d", "{}", "1.0");
        await svc.ApproveAsync(id);
        await svc.DeprecateAsync(id);
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task Experiment_Lifecycle()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Experiments.Experiment>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Experiments.Experiment>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new ExperimentService(repo, write);
        var id = await svc.CreateAsync("exp", "hyp", null, null);
        await svc.StartAsync(id);
        await svc.CompleteAsync(id, "{}");
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task TrainingJob_Lifecycle()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new TrainingJobService(repo, write);
        var id = await svc.CreateAsync(null, null, false, null);
        await svc.StartAsync(id);
        await svc.ReportProgressAsync(id, 0.5, 4096);
        await svc.ReportMetricsAsync(id, 5, 0.9, 10.0);
        await svc.CompleteAsync(id);
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task TrainingJob_Cancel_Fail()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new TrainingJobService(repo, write);
        var id1 = await svc.CreateAsync(null, null, false, null);
        await svc.CancelAsync(id1);
        var id2 = await svc.CreateAsync(null, null, false, null);
        await svc.FailAsync(id2, "err");
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ToolPolicy_Update_Delete()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Harness.ToolPolicy>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Harness.ToolPolicy>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new ToolPolicyService(repo, write);
        var id = await svc.CreateAsync("build", "read", "web", false, 10, 3);
        await svc.DeleteAsync(id);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task User_Update_Delete()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Entities.User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Entities.User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new UserService(repo, write);
        var id = await svc.CreateAsync("u", "u@x.com", "pass", false);
        await svc.UpdateAsync(id, "u2", "u2@x.com", false, true);
        await svc.DeleteAsync(id);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ModelBinding_Update_Delete()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Harness.ModelBinding>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Harness.ModelBinding>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new ModelBindingService(repo, write);
        var id = await svc.CreateAsync("openai", "gemma-4-12b", 4096, 32768, "off", 0.5);
        await svc.UpdateAsync(id, 8192, 65536, "on", 0.7);
        await svc.DeleteAsync(id);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task Agent_Update_Delete()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Entities.Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Entities.Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new AgentService(repo, write);
        var id = await svc.CreateAsync("ag", "m", "http://localhost:4124", 0.5, "on", "Build", "read", "sp");
        await svc.UpdateAsync(id, "ag2", "m2", "http://localhost:4124", 0.6, "off", "Build", "read,write", "sp2", true);
        await svc.DeleteAsync(id);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task HarnessAttachment_Delete()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessAttachment>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessAttachment>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new HarnessAttachmentService(repo, write);
        var id = await svc.CreateAsync(1, "Core", "general", "content");
        await svc.DeleteAsync(id);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task HarnessRun_Get_ListByProfileVersion()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessRun>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessRun>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new HarnessRunService(repo, write);
        var id = await svc.RecordAsync(1, 1, 1, "hash", 5, 2, 0, 100, 10.0);
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        var list = await svc.ListByProfileVersionAsync(1);
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task BridgeCapture_Get()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Harness.BridgeCapture>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Harness.BridgeCapture>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new BridgeCaptureService(repo, write);
        var id = await svc.RecordAsync("build", null, null, "hash");
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        System.IO.File.Delete(db);
    }
}
[Collection("Sequential")]
public class ExtraServiceTests2
{
    private static string NewDb() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ex2_{Guid.NewGuid()}.db");
    private static void Init(string db) => AppDb.Init(db);

    [Fact]
    public async Task HarnessProfile_Create_List_Get_AddVersion_Approve()
    {
        var db = NewDb(); Init(db);
        var profiles = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfile>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var versions = new GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfileVersion>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new HarnessProfileService(profiles, profiles, versions, versions);
        var id = await svc.CreateAsync("hp1", "desc");
        var list = await svc.ListAsync();
        Assert.Single(list);
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        var vid = await svc.AddVersionAsync(id, "Hybrid", "abc");
        var p2 = await svc.GetAsync(id);
        Assert.Single(p2!.Versions);
        await svc.ApproveVersionAsync(vid, "admin");
        p2 = await svc.GetAsync(id);
        Assert.Equal("admin", p2!.Versions[0].ApprovedBy);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task HarnessLinter_FindsAndAccepts()
    {
        var linter = new HarnessLinter();
        var f1 = linter.FindHardcodes("model gemma on :4124 with 64k context temp:0.5");
        Assert.NotEmpty(f1);
        var f2 = linter.FindHardcodes("Cumpra o enunciado antes de responder.");
        Assert.Empty(f2);
    }

    [Fact]
    public async Task TrainingWorkflow_UpdateAccuracy_Evaluate()
    {
        var db = NewDb(); Init(db);
        var repo = new GenericRepository<Rochas.AI.Manager.Domain.Entities.TrainingWorkflowCard>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new GenericRepository<Rochas.AI.Manager.Domain.Entities.TrainingWorkflowCard>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        var eval = new AccuracyEvaluator();
        var svc = new TrainingWorkflowService(repo, write, eval);
        var id = await svc.CreateAsync("card", null, 1, 0.9);
        await svc.UpdateAccuracyAsync(id, 0.95);
        var r = await svc.EvaluateAsync(id, "expected text", "actual text");
        Assert.NotNull(r);
        System.IO.File.Delete(db);
    }
}
