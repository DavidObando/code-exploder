using Xunit;

namespace CodeExploder.Storage.Tests;

public sealed class MigrationRunnerTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public void EmbeddedMigrationsAreDiscoveredInOrder()
    {
        var migrations = MigrationRunner.LoadEmbeddedMigrations().ToList();
        Assert.NotEmpty(migrations);
        Assert.Equal(migrations.OrderBy(m => m.Version).Select(m => m.Version), migrations.Select(m => m.Version));
        Assert.Equal(1, migrations[0].Version);
    }

    [Fact]
    public async Task ReapplyingIsANoOp()
    {
        // The fixture already migrated to head; a second pass must apply nothing.
        var applied = await new MigrationRunner(fixture.DataSource).ApplyPendingAsync();
        Assert.Empty(applied);
    }
}
