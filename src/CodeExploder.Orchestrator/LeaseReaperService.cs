using CodeExploder.Storage;

namespace CodeExploder.Orchestrator;

/// <summary>
/// Requeues jobs whose worker died mid-run (lease expiry). Terminal failures decrement
/// their parent joins inside the queue's reap statement, so a dead child can't wedge a
/// fan-out (docs/02-queue-and-events.md).
/// </summary>
public sealed class LeaseReaperService(JobQueue queue, ILogger<LeaseReaperService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reaped = await queue.RequeueExpiredAsync(LeaseTimeout, stoppingToken);
                if (reaped > 0)
                {
                    logger.LogWarning("Requeued {Count} expired job leases", reaped);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lease reap pass failed");
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
