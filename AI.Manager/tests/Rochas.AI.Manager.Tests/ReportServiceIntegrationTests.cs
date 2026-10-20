using Rochas.AI.Manager.Application.Services;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.AI.Manager.Infrastructure.Services;
using Xunit;

namespace Rochas.AI.Manager.Tests;

public class ReportServiceIntegrationTests
{
    [Fact]
    public async Task QueryAsync_TrainingJob_Empty()
    {
        var db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"rep_{Guid.NewGuid()}.db");
        AppDb.Init(db);
        var svc = new Rochas.AI.Manager.Infrastructure.Services.ReportService();
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
        var svc = new Rochas.AI.Manager.Infrastructure.Services.ReportService();
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
        var svc = new Rochas.AI.Manager.Infrastructure.Services.ReportService();
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
        var svc = new Rochas.AI.Manager.Infrastructure.Services.ReportService();
        var q = new ReportQuery { Entity = "TrainingJob", Page = 1, PageSize = 5 };
        var bytes = await svc.ExportAsync(q, "pdf");
        Assert.NotNull(bytes);
        Assert.NotNull(bytes);
        System.IO.File.Delete(db);
    }
}