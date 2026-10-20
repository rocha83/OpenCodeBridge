using Rochas.AI.Manager.Application.Services;
using Rochas.AI.Manager.Domain.Entities;
using Rochas.AI.Manager.Domain.Experiments;
using Rochas.AI.Manager.Domain.Harness;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Models;
using Rochas.AI.Manager.Domain.Datasets;
using Rochas.AI.Manager.Domain.Profiles;
using Rochas.AI.Manager.Domain.Training;
using Rochas.AI.Manager.Infrastructure.Data;
using Xunit;

namespace Rochas.AI.Manager.Tests;

[Collection("Sequential")]
public class ServiceIntegrationTests
{
    private static string NewDb() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"svc_{Guid.NewGuid()}.db");

    private static void InitDb(string db) => AppDb.Init(db);

    [Fact]
    public async Task AgentService_CreateListGet()
    {
        var db = NewDb(); InitDb(db);
        var svc = new AgentService(new Rochas.DapperRepository.GenericRepository<Agent>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString), new Rochas.DapperRepository.GenericRepository<Agent>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString));
        var id = await svc.CreateAsync("ag1", "openai/gemma-4-12b", "http://localhost:4124", 0.5, "on", "Build", "read,write", "sp");
        var list = await svc.ListAsync();
        Assert.Single(list);
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ModelBindingService_CreateListGet()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<ModelBinding>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new ModelBindingService(repo, repo);
        var id = await svc.CreateAsync("openai", "gemma-4-12b", 4096, 32768, "off", 0.5);
        var list = await svc.ListAsync();
        Assert.Single(list);
        var g = await svc.GetAsync(id);
        Assert.NotNull(g);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task DatasetService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<Dataset>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new Rochas.DapperRepository.GenericRepository<Dataset>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new DatasetService(repo, write);
        var id = await svc.CreateAsync("ds1", "jsonl", "1.0", "/tmp/ds.jsonl", null);
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ModelArtifactService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<ModelArtifact>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var write = new Rochas.DapperRepository.GenericRepository<ModelArtifact>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new ModelArtifactService(repo, write);
        var id = await svc.CreateAsync("ma1", "Gemma4", "12b", "gguf", "/tmp/m.gguf", true);
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task InstructionProfileService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<InstructionProfile>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new InstructionProfileService(repo, repo);
        var id = await svc.CreateAsync("ip1", "desc", "{}", "1.0");
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ToolPolicyService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<ToolPolicy>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new ToolPolicyService(repo, repo);
        var id = await svc.CreateAsync("build", "read,write", "websearch", false, 10, 3);
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task UserService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<User>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new UserService(repo, repo);
        var id = await svc.CreateAsync("u1", "u1@x.com", "pass", false);
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task TrainingJobService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<TrainingJob>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new TrainingJobService(repo, repo);
        var id = await svc.CreateAsync(null, null, false, null);
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ExperimentService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<Experiment>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new ExperimentService(repo, repo);
        var id = await svc.CreateAsync("exp1", "hyp", null, null);
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task HarnessAttachmentService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<HarnessAttachment>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new HarnessAttachmentService(repo, repo);
        var id = await svc.CreateAsync(1, "Core", "general", "content");
        var list = await svc.ListByVersionAsync(1);
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task HarnessRunService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<HarnessRun>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new HarnessRunService(repo, repo);
        var id = await svc.RecordAsync(1, 1, 1, "hash", 5, 2, 0, 100, 10.0);
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task BridgeCaptureService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<BridgeCapture>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new BridgeCaptureService(repo, repo);
        var id = await svc.RecordAsync("build", null, null, "hash123");
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task TrainingPatternService_CreateList()
    {
        var db = NewDb(); InitDb(db);
        var repo = new Rochas.DapperRepository.GenericRepository<TrainingPattern>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var svc = new TrainingPatternService(repo, repo);
        var id = await svc.CreateAsync("pat1", null, null, "{}");
        var list = await svc.ListAsync();
        Assert.Single(list);
        System.IO.File.Delete(db);
    }
}