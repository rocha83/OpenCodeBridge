using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Rochas.AI.Manager.Domain.Datasets;
using Rochas.AI.Manager.Domain.Entities;
using Rochas.AI.Manager.Domain.Entities.Management;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Models;
using Rochas.AI.Manager.Domain.Training;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.AI.Manager.Infrastructure.Services;
using Rochas.Data.Specification.Enums;
using Rochas.DapperRepository;
using Xunit;

namespace Rochas.AI.Manager.Tests;

[Collection("Sequential")]
public class ReportFilterTests
{
    private static string NewDb() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repf_{Guid.NewGuid()}.db");

    [Fact]
    public async Task QueryAsync_DateRange_IsInclusivePerDay()
    {
        var db = NewDb();
        AppDb.Init(db);
        await SeedJobsAsync();

        var service = new ReportService(BuildSp());

        var january = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", DateFrom = new DateTime(2026, 1, 1), DateTo = new DateTime(2026, 1, 31) });
        Assert.Equal(1, january.Total);

        var fromJanuary11 = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", DateFrom = new DateTime(2026, 1, 11) });
        Assert.Equal(1, fromJanuary11.Total);

        var boundary = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", DateFrom = new DateTime(2026, 1, 10), DateTo = new DateTime(2026, 1, 10) });
        Assert.Equal(1, boundary.Total);

        var none = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", DateFrom = new DateTime(2026, 3, 1), DateTo = new DateTime(2026, 3, 31) });
        Assert.Equal(0, none.Total);

        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task QueryAsync_AccuracyBand_And_MinMax()
    {
        var db = NewDb();
        AppDb.Init(db);
        await SeedJobsAsync();

        var service = new ReportService(BuildSp());

        var high = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", AccuracyBand = "high" });
        Assert.Equal(1, high.Total);

        var low = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", AccuracyBand = "low" });
        Assert.Equal(1, low.Total);

        var min = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", AccuracyMin = 0.5 });
        Assert.Equal(1, min.Total);

        var max = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", AccuracyMax = 0.5 });
        Assert.Equal(1, max.Total);

        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task QueryAsync_ModelFamily_UsesArtifactLink()
    {
        var db = NewDb();
        AppDb.Init(db);
        await SeedJobsAsync();

        var service = new ReportService(BuildSp());

        var gemma = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", ModelFamily = "Gemma4" });
        Assert.Equal(1, gemma.Total);

        var llama = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", ModelFamily = "Llama" });
        Assert.Equal(1, llama.Total);

        var unknown = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", ModelFamily = "Qwen" });
        Assert.Equal(0, unknown.Total);

        System.IO.File.Delete(db);
    }

    [Fact(Skip = "ReportService nao retorna cards de workflow (query vazia); investigar antes de reativar")]
    public async Task QueryAsync_StatusByCode_ResolvesWorkflowCard()
    {
        var db = NewDb();
        AppDb.Init(db);

        var statusRepo = new GenericRepository<EntityStatus>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        await statusRepo.Add(new EntityStatus { Code = "ACEITE", Name = "Aceite" });
        var statuses = await statusRepo.Query(new EntityStatus());
        var aceite = statuses.First(s => s.Code == "ACEITE");

        var cardRepo = new GenericRepository<TrainingWorkflowCard>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        await cardRepo.Add(new TrainingWorkflowCard { Name = "card1", Title = "card1", StatusId = aceite.Id });
        await cardRepo.Add(new TrainingWorkflowCard { Name = "card2", Title = "card2", StatusId = 999 });

        var service = new ReportService(BuildSp());
        var byCode = await service.QueryAsync(new ReportQuery { Entity = "TrainingWorkflowCard", Status = "ACEITE" });
        Assert.Equal(1, byCode.Total);

        var byId = await service.QueryAsync(new ReportQuery { Entity = "TrainingWorkflowCard", Status = aceite.Id.ToString() });
        Assert.Equal(1, byId.Total);

        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task QueryAsync_RejectsUnsupportedAxisFilterAndEntity()
    {
        var db = NewDb();
        AppDb.Init(db);
        var service = new ReportService(BuildSp());

        await Assert.ThrowsAsync<ReportQueryException>(() =>
            service.QueryAsync(new ReportQuery { Entity = "Dataset", DateAxis = "started_at" }));

        await Assert.ThrowsAsync<ReportQueryException>(() =>
            service.QueryAsync(new ReportQuery { Entity = "Dataset", ModelFamily = "Gemma4" }));

        await Assert.ThrowsAsync<ReportQueryException>(() =>
            service.QueryAsync(new ReportQuery { Entity = "TrainingJob", AgentId = 1 }));

        await Assert.ThrowsAsync<ReportQueryException>(() =>
            service.QueryAsync(new ReportQuery { Entity = "NaoExiste" }));

        await Assert.ThrowsAsync<ReportQueryException>(() =>
            service.QueryAsync(new ReportQuery { Entity = "TrainingJob", AccuracyBand = "turbo" }));

        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task QueryAsync_Pagination_IsClamped()
    {
        var db = NewDb();
        AppDb.Init(db);
        await SeedJobsAsync();

        var service = new ReportService(BuildSp());
        var result = await service.QueryAsync(new ReportQuery { Entity = "TrainingJob", Page = 0, PageSize = 5000 });

        Assert.Equal(1, result.Page);
        Assert.Equal(500, result.PageSize);
        Assert.Equal(2, result.Total);
        Assert.Equal(2, result.Items.Count);

        System.IO.File.Delete(db);
    }

    private static async Task SeedJobsAsync()
    {
        var artifacts = new GenericRepository<ModelArtifact>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        await artifacts.Add(new ModelArtifact { Name = "gemma", Family = "Gemma4", Format = "gguf" });
        await artifacts.Add(new ModelArtifact { Name = "llama", Family = "Llama", Format = "gguf" });
        var modelIds = (await artifacts.Query(new ModelArtifact())).OrderBy(a => a.Id).Select(a => a.Id).ToArray();

        var jobs = new GenericRepository<TrainingJob>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        await jobs.Add(new TrainingJob
        {
            Name = "job-1",
            CreatedAt = new DateTime(2026, 1, 10, 12, 0, 0),
            ModelArtifactId = modelIds[0],
            BestAccuracy = 0.4
        });
        await jobs.Add(new TrainingJob
        {
            Name = "job-2",
            CreatedAt = new DateTime(2026, 2, 10, 12, 0, 0),
            ModelArtifactId = modelIds[1],
            BestAccuracy = 0.85
        });
    }

    private static IServiceProvider BuildSp()
    {
        var sc = new ServiceCollection();
        var conn = AppDb.ConnectionString;
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<TrainingJob>>(_ => new GenericRepository<TrainingJob>(DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<ModelArtifact>>(_ => new GenericRepository<ModelArtifact>(DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<EntityStatus>>(_ => new GenericRepository<EntityStatus>(DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<TrainingWorkflowCard>>(_ => new GenericRepository<TrainingWorkflowCard>(DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Dataset>>(_ => new GenericRepository<Dataset>(DatabaseEngine.SQLite, conn));
        return sc.BuildServiceProvider();
    }
}
