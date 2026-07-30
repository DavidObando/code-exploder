using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace CodeExploder.Storage.Tests;

/// <summary>One throwaway Postgres container per test class, migrated to head.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // pgvector rides in the image because V0005 creates the vector extension.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17").Build();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        DataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await new MigrationRunner(DataSource).ApplyPendingAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}
