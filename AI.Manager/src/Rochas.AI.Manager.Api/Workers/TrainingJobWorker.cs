using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Training;
using Rochas.Telemetry;

namespace Rochas.AI.Manager.Api.Workers;

public sealed class TrainingJobWorker(
    IServiceProvider sp,
    ILogger<TrainingJobWorker> logger,
    Microsoft.Extensions.Configuration.IConfiguration config) : BackgroundService
{
    private readonly ServiceObserver _observer = new("AI.Manager.Training", logger);
    private readonly bool _diagnostic = (config["Diagnostic"] ?? Environment.GetEnvironmentVariable("DIAGNOSTIC")) is "1" or "true" or "True";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("TrainingJobWorker started");
        if (_diagnostic) { _observer.StartObserver(60, 1); logger.LogInformation("Diagnostic mode enabled"); }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = sp.CreateAsyncScope();
                var jobService = scope.ServiceProvider.GetRequiredService<ITrainingJobService>();
                var engine = scope.ServiceProvider.GetRequiredService<ITrainingEngine>();

                var jobs = await jobService.ListAsync(stoppingToken);
                var queued = jobs.FirstOrDefault(j => j.Status == "Queued");
                if (queued is not null)
                {
                    using var jobScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = queued.Id });
                    logger.LogInformation("Starting training job {JobId}", queued.Id);
                    try
                    {
                        await jobService.StartAsync(queued.Id, stoppingToken);
                        await _observer.ExecuteTimed($"training-job-{queued.Id}", () => engine.RunAsync(queued.Id, null, stoppingToken));
                        await jobService.CompleteAsync(queued.Id, stoppingToken);
                        logger.LogInformation("Training job {JobId} completed", queued.Id);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogWarning("Training job {JobId} cancelled", queued.Id);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Training job {JobId} failed: {Error}", queued.Id, ex.Message);
                        try { await jobService.FailAsync(queued.Id, ex.Message, CancellationToken.None); } catch { }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "TrainingJobWorker loop error");
            }

            try { await Task.Delay(PollInterval, stoppingToken); } catch (OperationCanceledException) { break; }
        }
        logger.LogInformation("TrainingJobWorker stopped");
    }
}
