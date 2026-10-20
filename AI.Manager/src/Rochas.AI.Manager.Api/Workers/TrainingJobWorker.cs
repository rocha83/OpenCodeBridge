using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Training;

namespace Rochas.AI.Manager.Api.Workers;

public sealed class TrainingJobWorker(
    IServiceProvider sp,
    ILogger<TrainingJobWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                    try
                    {
                        await jobService.StartAsync(queued.Id, stoppingToken);
                        await engine.RunAsync(queued.Id, null, stoppingToken);
                        await jobService.CompleteAsync(queued.Id, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Job {JobId} failed", queued.Id);
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
    }
}
