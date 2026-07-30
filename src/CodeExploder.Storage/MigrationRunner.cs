using System.Reflection;
using Npgsql;

namespace CodeExploder.Storage;

/// <summary>
/// Applies embedded SQL migrations (Migrations/V####__name.sql) in order, tracking them in
/// schema_migrations. Serialized across processes with a Postgres advisory lock so that
/// the gateway and all workers can run migrations at startup safely.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource dataSource)
{
    private const long AdvisoryLockKey = 0x434F_4445_5850; // "CODEXP"

    public async Task<IReadOnlyList<int>> ApplyPendingAsync(CancellationToken ct = default)
    {
        var applied = new List<int>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        await using (var lockCmd = new NpgsqlCommand($"select pg_advisory_lock({AdvisoryLockKey})", conn))
        {
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using (var ensure = new NpgsqlCommand(
                "create table if not exists schema_migrations (version int primary key, applied_at timestamptz not null default now())",
                conn))
            {
                await ensure.ExecuteNonQueryAsync(ct);
            }

            var known = new HashSet<int>();
            await using (var read = new NpgsqlCommand("select version from schema_migrations", conn))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    known.Add(reader.GetInt32(0));
                }
            }

            foreach (var (version, name, sql) in LoadEmbeddedMigrations())
            {
                if (known.Contains(version))
                {
                    continue;
                }

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var apply = new NpgsqlCommand(sql, conn, tx))
                {
                    await apply.ExecuteNonQueryAsync(ct);
                }

                await using (var record = new NpgsqlCommand(
                    "insert into schema_migrations (version) values ($1)", conn, tx))
                {
                    record.Parameters.AddWithValue(version);
                    await record.ExecuteNonQueryAsync(ct);
                }

                await tx.CommitAsync(ct);
                applied.Add(version);
                _ = name;
            }
        }
        finally
        {
            await using var unlock = new NpgsqlCommand($"select pg_advisory_unlock({AdvisoryLockKey})", conn);
            await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }

        return applied;
    }

    internal static IEnumerable<(int Version, string Name, string Sql)> LoadEmbeddedMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var entries = new List<(int, string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var marker = resource.IndexOf("Migrations.V", StringComparison.Ordinal);
            if (marker < 0 || !resource.EndsWith(".sql", StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resource[(marker + "Migrations.".Length)..];
            var sepIdx = fileName.IndexOf("__", StringComparison.Ordinal);
            if (sepIdx < 1 || !int.TryParse(fileName.AsSpan(1, sepIdx - 1), out var version))
            {
                throw new InvalidOperationException($"Migration resource has unparseable name: {resource}");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Cannot open migration resource: {resource}");
            using var readerStream = new StreamReader(stream);
            entries.Add((version, fileName, readerStream.ReadToEnd()));
        }

        return entries.OrderBy(e => e.Item1);
    }
}
