using CodeExploder.Storage;

namespace CodeExploder.Orchestrator;

/// <summary>Purges finished job rows on an hourly cadence (queue hygiene).</summary>
public sealed class RetentionService(JobQueue queue, ILogger<RetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetainFinished = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var purged = await queue.PurgeFinishedAsync(RetainFinished, stoppingToken);
                if (purged > 0)
                {
                    logger.LogInformation("Purged {Count} finished jobs", purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention pass failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
