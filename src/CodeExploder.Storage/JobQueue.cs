using Npgsql;

namespace CodeExploder.Storage;

public sealed record QueuedJob(
    Guid Id,
    string JobType,
    string PayloadJson,
    int Priority,
    int Attempts,
    int MaxAttempts,
    Guid? AnalysisId,
    Guid? UnblocksJobId);

/// <summary>
/// Postgres-backed job queue (docs/02-queue-and-events.md). Dequeue order is
/// priority desc then created_at, via FOR UPDATE SKIP LOCKED so any number of workers
/// can poll concurrently. Jobs whose worker dies are reaped by
/// <see cref="RequeueExpiredAsync"/> (run by the orchestrator).
///
/// The counting-join extension: a join job is enqueued 'blocked' with blocked_count=N;
/// each child carries unblocks_job_id and decrements the counter exactly once, on its
/// transition into a terminal status (succeeded, or failed with no retries left). At
/// zero the join flips to 'queued'. Terminal failure decrements too, so a dead child
/// can't wedge the run — the join proceeds and reports gaps.
/// </summary>
public sealed class JobQueue(NpgsqlDataSource dataSource)
{
    /// <summary>Decrements the parent join when a child reaches a terminal status.</summary>
    private const string UnblockCte = """
        update jobs j
        set blocked_count = j.blocked_count - 1,
            status = case when j.blocked_count - 1 = 0 then 'queued' else j.status end,
            updated_at = now()
        from terminal t
        where t.unblocks_job_id is not null
          and j.id = t.unblocks_job_id
          and j.status = 'blocked'
        """;

    public async Task<Guid> EnqueueAsync(
        string jobType,
        string payloadJson,
        int priority = 0,
        DateTimeOffset? availableAt = null,
        Guid? analysisId = null,
        Guid? unblocksJobId = null,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            """
            insert into jobs (id, job_type, payload, priority, available_at, analysis_id, unblocks_job_id)
            values ($1, $2, $3::jsonb, $4, $5, $6, $7)
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(jobType);
        cmd.Parameters.AddWithValue(payloadJson);
        cmd.Parameters.AddWithValue(priority);
        cmd.Parameters.AddWithValue((object?)availableAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)analysisId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)unblocksJobId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    /// <summary>
    /// Enqueues a join job that stays 'blocked' until <paramref name="blockedCount"/>
    /// children (enqueued with unblocksJobId = the returned id) reach a terminal status.
    /// </summary>
    public async Task<Guid> EnqueueBlockedAsync(
        string jobType,
        string payloadJson,
        int blockedCount,
        int priority = 0,
        Guid? analysisId = null,
        Guid? unblocksJobId = null,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(blockedCount, 0);
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            """
            insert into jobs (id, job_type, payload, priority, status, blocked_count, analysis_id, unblocks_job_id)
            values ($1, $2, $3::jsonb, $4, 'blocked', $5, $6, $7)
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(jobType);
        cmd.Parameters.AddWithValue(payloadJson);
        cmd.Parameters.AddWithValue(priority);
        cmd.Parameters.AddWithValue(blockedCount);
        cmd.Parameters.AddWithValue((object?)analysisId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)unblocksJobId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<QueuedJob?> TryDequeueAsync(
        IReadOnlyCollection<string> jobTypes,
        string workerId,
        CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            update jobs
            set status = 'running', locked_by = $1, locked_at = now(),
                attempts = attempts + 1, updated_at = now()
            where id = (
                select id from jobs
                where status = 'queued' and job_type = any($2)
                  and (available_at is null or available_at <= now())
                order by priority desc, created_at asc
                limit 1
                for update skip locked)
            returning id, job_type, payload::text, priority, attempts, max_attempts, analysis_id, unblocks_job_id
            """);
        cmd.Parameters.AddWithValue(workerId);
        cmd.Parameters.AddWithValue(jobTypes.ToArray());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new QueuedJob(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7));
    }

    /// <summary>
    /// Marks the job succeeded and decrements its parent join, atomically. The
    /// status='running' guard makes completion idempotent: a job the reaper already
    /// requeued (or a double-complete) is a no-op and never double-decrements.
    /// </summary>
    public async Task CompleteAsync(Guid jobId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            with terminal as (
                update jobs
                set status = 'succeeded', locked_by = null, updated_at = now()
                where id = $1 and status = 'running'
                returning unblocks_job_id
            )
            {UnblockCte}
            """);
        cmd.Parameters.AddWithValue(jobId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Requeues if attempts remain, otherwise marks failed — and only that terminal
    /// failure decrements the parent join (a retryable failure is not terminal).
    /// </summary>
    public async Task FailAsync(Guid jobId, string errorMessage, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"""
            with terminal as (
                update jobs
                set status = case when attempts >= max_attempts then 'failed' else 'queued' end,
                    locked_by = null, locked_at = null, last_error = $2, updated_at = now()
                where id = $1 and status = 'running'
                returning case when status = 'failed' then unblocks_job_id end as unblocks_job_id
            )
            {UnblockCte}
            """);
        cmd.Parameters.AddWithValue(jobId);
        cmd.Parameters.AddWithValue(errorMessage);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reaps jobs whose worker stopped heartbeating. Processed row by row so that two
    /// reaped children of the same join both decrement it (a single UPDATE…FROM would
    /// apply only one of the matching source rows).
    /// </summary>
    public async Task<int> RequeueExpiredAsync(TimeSpan leaseTimeout, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var expired = new List<Guid>();
        await using (var find = new NpgsqlCommand(
            "select id from jobs where status = 'running' and locked_at < now() - $1 for update skip locked",
            conn, tx))
        {
            find.Parameters.AddWithValue(leaseTimeout);
            await using var reader = await find.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                expired.Add(reader.GetGuid(0));
            }
        }

        foreach (var id in expired)
        {
            await using var reap = new NpgsqlCommand(
                $"""
                with terminal as (
                    update jobs
                    set status = case when attempts >= max_attempts then 'failed' else 'queued' end,
                        locked_by = null, locked_at = null,
                        last_error = coalesce(last_error, '') || ' [lease expired]',
                        updated_at = now()
                    where id = $1 and status = 'running'
                    returning case when status = 'failed' then unblocks_job_id end as unblocks_job_id
                )
                {UnblockCte}
                """, conn, tx);
            reap.Parameters.AddWithValue(id);
            await reap.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return expired.Count;
    }

    /// <summary>Purges finished jobs older than the window (queue hygiene).</summary>
    public async Task<int> PurgeFinishedAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "delete from jobs where status in ('succeeded','failed','cancelled') and updated_at < now() - $1");
        cmd.Parameters.AddWithValue(olderThan);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>True when any of the given job types is queued/blocked/running for the analysis.</summary>
    public async Task<bool> HasActiveJobAsync(
        Guid analysisId, IReadOnlyCollection<string> jobTypes, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select 1 from jobs
            where analysis_id = $1 and job_type = any($2)
              and status in ('queued','blocked','running')
            limit 1
            """);
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(jobTypes.ToArray());
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Queue depth for the status bar: (queued, running) counts.</summary>
    public async Task<(long Queued, long Running)> DepthAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select count(*) filter (where status in ('queued','blocked')),
                   count(*) filter (where status = 'running')
            from jobs
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
