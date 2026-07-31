using CodeExploder.Storage.Bundles;

namespace CodeExploder.Gateway;

/// <summary>
/// Installs demo bundles at startup (docs/08 §M7): every *.cxbundle.gz in Seed:Dir
/// becomes a ready session for Seed:Subject unless that user already has the same
/// repo@sha. Reviewers land on a populated app with zero GPU time spent.
/// </summary>
public sealed class SeedService(
    BundleImporter importer,
    IConfiguration config,
    ILogger<SeedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Seed:Enabled", true))
        {
            return;
        }

        // Probe the configured path, then the app base directory (the container image
        // ships seeds/ next to the binaries; `dotnet run` CWD is the project dir).
        var configured = config["Seed:Dir"] ?? "seeds";
        var dir = new[] { configured, Path.Combine(AppContext.BaseDirectory, configured) }
            .FirstOrDefault(Directory.Exists);
        if (dir is null)
        {
            logger.LogInformation("Seed directory '{Dir}' not found; skipping demo seeding", configured);
            return;
        }

        var subject = config["Seed:Subject"] ?? "dev@local";
        foreach (var path in Directory.EnumerateFiles(dir, "*.cxbundle.gz").Order())
        {
            try
            {
                var doc = await BundleImporter.LoadAsync(path, stoppingToken);
                if (await importer.IsInstalledAsync(doc, subject, stoppingToken))
                {
                    continue;
                }

                var sessionId = await importer.ImportAsync(doc, subject, stoppingToken);
                logger.LogInformation(
                    "Seeded demo {Owner}/{Name}@{Sha} as session {SessionId} for {Subject}",
                    doc.RepoOwner, doc.RepoName, doc.CommitSha[..Math.Min(7, doc.CommitSha.Length)],
                    sessionId, subject);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Failed to seed bundle {Path}", path);
            }
        }
    }
}
