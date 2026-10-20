using Rochas.AI.Manager.Application.Services;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.AI.Manager.Infrastructure.Services;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace Rochas.AI.Manager.Tests;

[Collection("Sequential")]
public class ReportServiceIntegrationTests
{
    [Fact]
    public async Task QueryAsync_TrainingJob_Empty()
    {
        var db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rep_{Guid.NewGuid()}.db");
        AppDb.Init(db);
        var svc = new Rochas.AI.Manager.Infrastructure.Services.ReportService(BuildSp(db));
        var q = new ReportQuery { Entity = "TrainingJob", Page = 1, PageSize = 5 };
        var r = await svc.QueryAsync(q);
        Assert.NotNull(r);
        Assert.Equal(0, r.Total);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ExportAsync_Csv_ReturnsBytes()
    {
        var db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rep2_{Guid.NewGuid()}.db");
        AppDb.Init(db);
        var svc = new Rochas.AI.Manager.Infrastructure.Services.ReportService(BuildSp(db));
        var q = new ReportQuery { Entity = "TrainingJob", Page = 1, PageSize = 5 };
        var bytes = await svc.ExportAsync(q, "csv");
        Assert.NotNull(bytes);
        Assert.NotNull(bytes);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ExportAsync_Excel_ReturnsBytes()
    {
        var db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rep3_{Guid.NewGuid()}.db");
        AppDb.Init(db);
        var svc = new Rochas.AI.Manager.Infrastructure.Services.ReportService(BuildSp(db));
        var q = new ReportQuery { Entity = "TrainingJob", Page = 1, PageSize = 5 };
        var bytes = await svc.ExportAsync(q, "excel");
        Assert.NotNull(bytes);
        Assert.NotNull(bytes);
        System.IO.File.Delete(db);
    }

    [Fact]
    public async Task ExportAsync_Pdf_ReturnsBytes()
    {
        var db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rep4_{Guid.NewGuid()}.db");
        AppDb.Init(db);
        var svc = new Rochas.AI.Manager.Infrastructure.Services.ReportService(BuildSp(db));
        var q = new ReportQuery { Entity = "TrainingJob", Page = 1, PageSize = 5 };
        var bytes = await svc.ExportAsync(q, "pdf");
        Assert.NotNull(bytes);
        Assert.NotNull(bytes);
        System.IO.File.Delete(db);
    }

    private static IServiceProvider BuildSp(string db)
    {
        var sc = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        AppDb.Init(db);
        var conn = AppDb.ConnectionString;
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Training.TrainingJob>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Experiments.Experiment>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Experiments.Experiment>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessRun>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessRun>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Entities.TrainingWorkflowCard>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Entities.TrainingWorkflowCard>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Models.ModelArtifact>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Models.ModelArtifact>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Datasets.Dataset>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Datasets.Dataset>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Profiles.InstructionProfile>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Profiles.InstructionProfile>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfile>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfile>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Harness.ToolPolicy>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.ToolPolicy>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Harness.ModelBinding>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.ModelBinding>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Harness.BridgeCapture>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.BridgeCapture>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Entities.User>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Entities.User>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        sc.AddScoped<Rochas.Data.Specification.Interfaces.IGenericRepository<Rochas.AI.Manager.Domain.Entities.Agent>>(_ => new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Entities.Agent>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, conn));
        return sc.BuildServiceProvider();
    }

}