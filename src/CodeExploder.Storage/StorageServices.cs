using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeExploder.Storage;

public static class StorageServices
{
    /// <summary>
    /// Registers the Postgres data source, migration runner, job queue, and stores.
    /// Expects ConnectionStrings:Postgres.
    /// </summary>
    public static IServiceCollection AddCodeExploderStorage(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<JobQueue>();
        services.AddSingleton<SessionStore>();
        return services;
    }

    /// <summary>Turns this host into a session-event publisher (Postgres NOTIFY bus).</summary>
    public static IServiceCollection AddSessionEventPublishing(this IServiceCollection services)
    {
        services.AddSingleton<ISessionEventBus>(sp => new PgSessionEventBus(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetService<ILogger<PgSessionEventBus>>()));
        return services;
    }
}
