namespace CodeExploder.Workers.Analysis;

/// <summary>
/// Reaps stale workspace checkouts (docs/07 §operations): workspaces are re-derivable
/// caches, so anything untouched for the retention window is deleted. Runs in this
/// worker because it owns the workspaces volume.
/// </summary>
public sealed class WorkspaceJanitorService(
    IConfiguration config,
    ILogger<WorkspaceJanitorService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly string _workspacesRoot = config["Workspaces:Root"]
        ?? Path.Combine(Path.GetTempPath(), "code-exploder", "workspaces");

    private readonly TimeSpan _retention = TimeSpan.FromDays(
        config.GetValue("Workspaces:RetentionDays", 7));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Sweep();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workspace sweep failed");
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

    private void Sweep()
    {
        if (!Directory.Exists(_workspacesRoot))
        {
            return;
        }

        var cutoff = DateTimeOffset.UtcNow - _retention;
        foreach (var dir in Directory.EnumerateDirectories(_workspacesRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    Directory.Delete(dir, recursive: true);
                    logger.LogInformation("Reaped stale workspace {Dir}", Path.GetFileName(dir));
                }
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not reap workspace {Dir}", dir);
            }
        }
    }
}
