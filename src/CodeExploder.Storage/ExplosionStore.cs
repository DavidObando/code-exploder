using Npgsql;

namespace CodeExploder.Storage;

public sealed record ExplosionRow(
    Guid Id, Guid AnalysisId, Guid ExperienceId, Guid ComponentId, Guid? SectionId,
    Guid? ParentExplosionId, int Depth, string Trigger, string Status,
    int SectionsTotal, int SectionsReady, Guid? QueueJobId, string? Error);

/// <summary>
/// Raw-SQL store for scope explosions (deep dives, M10). One row per
/// (experience, component) — the dedup key, the status machine, and the subtree
/// progress counters. Rows cascade away with the analysis.
/// </summary>
public sealed class ExplosionStore(NpgsqlDataSource dataSource)
{
    private const string RowSelect = """
        select id, analysis_id, experience_id, component_id, section_id,
               parent_explosion_id, depth, trigger, status,
               sections_total, sections_ready, queue_job_id, error
        from explosions
        """;

    /// <summary>Inserts a new explosion; null when one already exists for the component
    /// (the unique index resolves double-POST races — the loser re-fetches).</summary>
    public async Task<Guid?> TryCreateAsync(
        Guid analysisId, Guid experienceId, Guid componentId, Guid? parentExplosionId,
        int depth, string trigger, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            """
            insert into explosions (id, analysis_id, experience_id, component_id, parent_explosion_id, depth, trigger)
            values ($1, $2, $3, $4, $5, $6, $7)
            on conflict (experience_id, component_id) do nothing
            returning id
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(experienceId);
        cmd.Parameters.AddWithValue(componentId);
        cmd.Parameters.AddWithValue((object?)parentExplosionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(depth);
        cmd.Parameters.AddWithValue(trigger);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<ExplosionRow?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"{RowSelect} where id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<ExplosionRow?> GetByComponentAsync(
        Guid experienceId, Guid componentId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"{RowSelect} where experience_id = $1 and component_id = $2");
        cmd.Parameters.AddWithValue(experienceId);
        cmd.Parameters.AddWithValue(componentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<ExplosionRow>> ListForExperienceAsync(
        Guid experienceId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"{RowSelect} where experience_id = $1 order by created_at");
        cmd.Parameters.AddWithValue(experienceId);
        var rows = new List<ExplosionRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public async Task SetStatusAsync(
        Guid id, string status, string? error = null, bool finished = false, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            update explosions
            set status = $2, error = $3, finished_at = case when $4 then now() else finished_at end
            where id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue((object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue(finished);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetSectionAsync(Guid id, Guid sectionId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("update explosions set section_id = $2 where id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(sectionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetTriggerAsync(Guid id, string trigger, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("update explosions set trigger = $2 where id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(trigger);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetQueueJobAsync(Guid id, Guid jobId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("update explosions set queue_job_id = $2 where id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(jobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetSectionsTotalAsync(Guid id, int total, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update explosions set sections_total = $2, sections_ready = 0 where id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(total);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Increments sections_ready and returns (ready, total) for progress events.</summary>
    public async Task<(int Ready, int Total)> IncrementSectionsReadyAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update explosions set sections_ready = sections_ready + 1 where id = $1 returning sections_ready, sections_total");
        cmd.Parameters.AddWithValue(id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>The owning session, for gateway ownership checks on explosion routes.</summary>
    public async Task<Guid?> GetSessionIdAsync(Guid explosionId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select e.session_id from explosions x
            join experiences e on e.id = x.experience_id
            where x.id = $1
            """);
        cmd.Parameters.AddWithValue(explosionId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    /// <summary>Queued/running explosions for this analysis — the concurrency guard.</summary>
    public async Task<int> CountActiveForAnalysisAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select count(*) from explosions where analysis_id = $1 and status in ('queued','running')");
        cmd.Parameters.AddWithValue(analysisId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Resets a FAILED explosion for a relaunch: status back to queued, error and
    /// counters cleared. Returns false when the row isn't failed (re-checked under
    /// lock so concurrent retries relaunch exactly once).
    /// </summary>
    public async Task<bool> ResetForRetryAsync(Guid id, string trigger, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var check = new NpgsqlCommand(
            "select 1 from explosions where id = $1 and status = 'failed' for update", conn, tx))
        {
            check.Parameters.AddWithValue(id);
            if (await check.ExecuteScalarAsync(ct) is null)
            {
                return false;
            }
        }

        await using (var reset = new NpgsqlCommand(
            """
            update explosions
            set status = 'queued', trigger = $2, error = null,
                sections_total = 0, sections_ready = 0, queue_job_id = null, finished_at = null
            where id = $1
            """, conn, tx))
        {
            reset.Parameters.AddWithValue(id);
            reset.Parameters.AddWithValue(trigger);
            await reset.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    private static ExplosionRow Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetGuid(3),
        reader.IsDBNull(4) ? null : reader.GetGuid(4),
        reader.IsDBNull(5) ? null : reader.GetGuid(5),
        reader.GetInt32(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetInt32(9),
        reader.GetInt32(10),
        reader.IsDBNull(11) ? null : reader.GetGuid(11),
        reader.IsDBNull(12) ? null : reader.GetString(12));
}
