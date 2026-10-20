using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.TrainingEngine;
using Rochas.AI.Manager.Infrastructure.Data;
using Rochas.AI.Manager.Domain.Training;
using Rochas.Data.Specification.Interfaces;
using System.IO;

namespace Rochas.AI.Manager.Tests;

[Collection("db")]
public class TrainingEngineTests
{
    [Fact]
    public async Task Run_Synthetic_Completes()
    {
        var db = Path.Combine(Path.GetTempPath(), $"te_{Guid.NewGuid()}.db");
        AppDb.Init(db);
        var jobRepo = new Rochas.DapperRepository.GenericRepository<TrainingJob>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var jobWrite = new Rochas.DapperRepository.GenericRepository<TrainingJob>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var jobSvc = new Rochas.AI.Manager.Application.Services.TrainingJobService(jobRepo, jobWrite);
        var modelRepo = new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Models.ModelArtifact>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var modelWrite = new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Models.ModelArtifact>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var modelSvc = new Rochas.AI.Manager.Application.Services.ModelArtifactService(modelRepo, modelWrite);
        var datasetRepo = new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Datasets.Dataset>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var datasetWrite = new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Datasets.Dataset>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var datasetSvc = new Rochas.AI.Manager.Application.Services.DatasetService(datasetRepo, datasetWrite);
        var harnessRepo = new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfile>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var harnessWrite = new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfile>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var harnessVerRepo = new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfileVersion>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var harnessVerWrite = new Rochas.DapperRepository.GenericRepository<Rochas.AI.Manager.Domain.Harness.HarnessProfileVersion>(Rochas.Data.Specification.Enums.DatabaseEngine.SQLite, AppDb.ConnectionString);
        var harnessSvc = new Rochas.AI.Manager.Application.Services.HarnessProfileService(harnessRepo, harnessWrite, harnessVerRepo, harnessVerWrite);
        var backend = new TorchSharpTrainingBackend();
        var runner = new Rochas.AI.Manager.TrainingEngine.TrainingRunner();
        var engine = new Rochas.AI.Manager.TrainingEngine.TrainingEngine(jobSvc, modelSvc, datasetSvc, harnessSvc, backend, runner);
        var jobId = await jobSvc.CreateAsync(null, null, false, null);
        await engine.RunAsync(jobId);
        var job = await jobSvc.GetAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal("Completed", job!.Status);
        Assert.True(job!.BestAccuracy >= 0);
        File.Delete(db);
    }
}
