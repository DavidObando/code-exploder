using CodeExploder.Domain;
using Npgsql;

namespace CodeExploder.Storage;

/// <summary>Raw-SQL store for users, repos, analyses, and sessions (M0 subset).</summary>
public sealed class SessionStore(NpgsqlDataSource dataSource)
{
    // Progress = user-completed / total depth-0 sections of the latest experience
    // (docs/04). The analyses.sections_* counters drive generation progress events only.
    private const string SessionViewSelect = """
        select s.id, s.kind, s.title, r.owner, r.name, a.pr_number, s.status,
               s.failure_reason, s.created_at, s.analysis_id,
               coalesce(prog.total, 0), coalesce(prog.completed, 0)
        from sessions s
        join analyses a on a.id = s.analysis_id
        join repos r on r.id = a.repo_id
        left join lateral (
            select count(*)::int as total,
                   (count(*) filter (where sp.state = 'completed'))::int as completed
            from experiences e
            join sections sec on sec.experience_id = e.id and sec.depth = 0
            left join section_progress sp on sp.section_id = sec.id and sp.user_id = s.user_id
            where e.session_id = s.id
              and e.version = (select max(version) from experiences e2 where e2.session_id = s.id)
        ) prog on true
        """;

    public async Task<Guid> GetOrCreateUserAsync(string subject, string displayName, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            insert into users (id, subject, display_name) values ($1, $2, $3)
            on conflict (subject) do update set display_name = excluded.display_name
            returning id
            """);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(subject);
        cmd.Parameters.AddWithValue(displayName);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<Guid> GetOrCreateRepoAsync(string owner, string name, string url, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            insert into repos (id, owner, name, url) values ($1, $2, $3, $4)
            on conflict (owner, name) do update set url = excluded.url
            returning id
            """);
        cmd.Parameters.AddWithValue(Guid.NewGuid());
        cmd.Parameters.AddWithValue(owner);
        cmd.Parameters.AddWithValue(name);
        cmd.Parameters.AddWithValue(url);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<Guid> CreateAnalysisAsync(Guid repoId, string kind, int? prNumber, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            "insert into analyses (id, repo_id, kind, pr_number) values ($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(repoId);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue((object?)prNumber ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<Guid> CreateSessionAsync(
        Guid userId, Guid analysisId, string kind, string title, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            "insert into sessions (id, user_id, analysis_id, kind, title) values ($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue(title);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<SessionView>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"{SessionViewSelect} where s.user_id = $1 order by s.created_at desc");
        cmd.Parameters.AddWithValue(userId);
        var sessions = new List<SessionView>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(ReadView(reader));
        }

        return sessions;
    }

    /// <summary>Ownership-scoped lookup: returns null for other users' sessions (routes 404).</summary>
    public async Task<SessionView?> GetForUserAsync(Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            $"{SessionViewSelect} where s.id = $1 and s.user_id = $2");
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadView(reader) : null;
    }

    /// <summary>Resolves a session's owning user subject; used by the event relay's user-group routing.</summary>
    public async Task<string?> GetOwnerSubjectAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select u.subject from sessions s join users u on u.id = s.user_id where s.id = $1");
        cmd.Parameters.AddWithValue(sessionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<bool> IsOwnedByAsync(Guid sessionId, string subject, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select 1 from sessions s join users u on u.id = s.user_id where s.id = $1 and u.subject = $2");
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(subject);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task DeleteAsync(Guid sessionId, CancellationToken ct = default)
    {
        // Deleting the analysis cascades the session (docs/03-data-model.md keeps the
        // big analysis-side rows bounded this way); events are cleaned by session id.
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid? analysisId = null;
        await using (var find = new NpgsqlCommand("select analysis_id from sessions where id = $1", conn, tx))
        {
            find.Parameters.AddWithValue(sessionId);
            analysisId = await find.ExecuteScalarAsync(ct) as Guid?;
        }

        if (analysisId is { } aid)
        {
            await using (var jobs = new NpgsqlCommand("delete from jobs where analysis_id = $1", conn, tx))
            {
                jobs.Parameters.AddWithValue(aid);
                await jobs.ExecuteNonQueryAsync(ct);
            }

            await using (var events = new NpgsqlCommand("delete from pipeline_events where session_id = $1", conn, tx))
            {
                events.Parameters.AddWithValue(sessionId);
                await events.ExecuteNonQueryAsync(ct);
            }

            await using (var analysis = new NpgsqlCommand("delete from analyses where id = $1", conn, tx))
            {
                analysis.Parameters.AddWithValue(aid);
                await analysis.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Resets a failed session for a fresh run: repoints it at a brand-new analysis and
    /// deletes the old one (the cascade wipes files/chunks/summaries/experiences), clears
    /// the session's event log, and recovers the original acquire payload's gitRef so the
    /// retry targets the same ref. Returns null when the session isn't failed (the status
    /// is re-checked under lock so concurrent retries enqueue exactly one acquire).
    /// </summary>
    public async Task<(Guid AnalysisId, string? GitRef)?> RetryAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        Guid oldAnalysisId;
        Guid repoId;
        string kind;
        int? prNumber;
        await using (var find = new NpgsqlCommand(
            """
            select a.id, a.repo_id, a.kind, a.pr_number
            from sessions s join analyses a on a.id = s.analysis_id
            where s.id = $1 and s.status = 'failed'
            for update of s
            """, conn, tx))
        {
            find.Parameters.AddWithValue(sessionId);
            await using var reader = await find.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                return null;
            }

            oldAnalysisId = reader.GetGuid(0);
            repoId = reader.GetGuid(1);
            kind = reader.GetString(2);
            prNumber = reader.IsDBNull(3) ? null : reader.GetInt32(3);
        }

        string? gitRef = null;
        await using (var refCmd = new NpgsqlCommand(
            "select payload->>'gitRef' from jobs where analysis_id = $1 and job_type = 'acquire' order by created_at limit 1",
            conn, tx))
        {
            refCmd.Parameters.AddWithValue(oldAnalysisId);
            gitRef = await refCmd.ExecuteScalarAsync(ct) as string;
        }

        var newAnalysisId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            "insert into analyses (id, repo_id, kind, pr_number) values ($1, $2, $3, $4)", conn, tx))
        {
            insert.Parameters.AddWithValue(newAnalysisId);
            insert.Parameters.AddWithValue(repoId);
            insert.Parameters.AddWithValue(kind);
            insert.Parameters.AddWithValue((object?)prNumber ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var repoint = new NpgsqlCommand(
            "update sessions set analysis_id = $2, status = 'queued', failure_reason = null where id = $1", conn, tx))
        {
            repoint.Parameters.AddWithValue(sessionId);
            repoint.Parameters.AddWithValue(newAnalysisId);
            await repoint.ExecuteNonQueryAsync(ct);
        }

        await using (var jobs = new NpgsqlCommand("delete from jobs where analysis_id = $1", conn, tx))
        {
            jobs.Parameters.AddWithValue(oldAnalysisId);
            await jobs.ExecuteNonQueryAsync(ct);
        }

        await using (var events = new NpgsqlCommand("delete from pipeline_events where session_id = $1", conn, tx))
        {
            events.Parameters.AddWithValue(sessionId);
            await events.ExecuteNonQueryAsync(ct);
        }

        await using (var old = new NpgsqlCommand("delete from analyses where id = $1", conn, tx))
        {
            old.Parameters.AddWithValue(oldAnalysisId);
            await old.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (newAnalysisId, gitRef);
    }

    public async Task SetSessionStatusAsync(
        Guid sessionId, string status, string? failureReason = null, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update sessions set status = $2, failure_reason = $3 where id = $1");
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue((object?)failureReason ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetAnalysisStatusAsync(
        Guid analysisId, string status, string? error = null, bool finished = false, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            update analyses
            set status = $2, error = $3, finished_at = case when $4 then now() else finished_at end
            where id = $1
            """);
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue((object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue(finished);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddSectionsTotalAsync(Guid analysisId, int delta, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update analyses set sections_total = sections_total + $2 where id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(delta);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetSectionsTotalAsync(Guid analysisId, int total, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update analyses set sections_total = $2 where id = $1");
        cmd.Parameters.AddWithValue(analysisId);
        cmd.Parameters.AddWithValue(total);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Increments sections_ready and returns (ready, total) for progress events.</summary>
    public async Task<(int Ready, int Total)> IncrementSectionsReadyAsync(Guid analysisId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update analyses set sections_ready = sections_ready + 1 where id = $1 returning sections_ready, sections_total");
        cmd.Parameters.AddWithValue(analysisId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static SessionView ReadView(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetInt32(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetGuid(9),
        reader.GetInt32(10),
        reader.GetInt32(11));
}
