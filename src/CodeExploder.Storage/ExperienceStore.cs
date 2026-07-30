using Npgsql;
using NpgsqlTypes;

namespace CodeExploder.Storage;

public sealed record SectionRow(
    Guid Id, string Slug, string Kind, string Title, string Summary,
    int Ord, int Depth, Guid? ParentSectionId, int EstimatedMinutes, string Status, string MyState,
    bool HasQuiz, int? QuizBestPct);

public sealed record ExperienceRow(
    Guid Id, int Version, string CommitSha, string Model, DateTimeOffset GeneratedAt);

public sealed record BlockRow(Guid Id, int Ord, string Type, string DataJson);

public sealed record SectionDetailRow(
    Guid Id, string Slug, string Title, string Kind, string Status, Guid? QuizId,
    IReadOnlyList<BlockRow> Blocks);

/// <summary>Raw-SQL store for the experience side: experiences → sections → blocks + per-user progress.</summary>
public sealed class ExperienceStore(NpgsqlDataSource dataSource)
{
    public async Task<Guid> CreateExperienceAsync(
        Guid sessionId, string commitSha, string model, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            """
            insert into experiences (id, session_id, version, commit_sha, model)
            values ($1, $2,
                (select coalesce(max(version), 0) + 1 from experiences where session_id = $2),
                $3, $4)
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(commitSha);
        cmd.Parameters.AddWithValue(model);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<Guid> CreateSectionAsync(
        Guid experienceId, int ord, string slug, string kind, string title, string summary,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            """
            insert into sections (id, experience_id, ord, slug, kind, title, summary)
            values ($1, $2, $3, $4, $5, $6, $7)
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(experienceId);
        cmd.Parameters.AddWithValue(ord);
        cmd.Parameters.AddWithValue(slug);
        cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue(title);
        cmd.Parameters.AddWithValue(summary);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task SetSectionStatusAsync(Guid sectionId, string status, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("update sections set status = $2 where id = $1");
        cmd.Parameters.AddWithValue(sectionId);
        cmd.Parameters.AddWithValue(status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Publishes a generated section atomically: blocks + metadata + ready status.</summary>
    public async Task CompleteSectionAsync(
        Guid sectionId,
        string summary,
        int estimatedMinutes,
        IReadOnlyList<(string Type, string DataJson)> blocks,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var clear = new NpgsqlCommand("delete from blocks where section_id = $1", conn, tx))
        {
            clear.Parameters.AddWithValue(sectionId);
            await clear.ExecuteNonQueryAsync(ct);
        }

        var ord = 0;
        foreach (var (type, dataJson) in blocks)
        {
            await using var insert = new NpgsqlCommand(
                "insert into blocks (id, section_id, ord, type, data) values ($1, $2, $3, $4, $5)", conn, tx);
            insert.Parameters.AddWithValue(Guid.NewGuid());
            insert.Parameters.AddWithValue(sectionId);
            insert.Parameters.AddWithValue(ord++);
            insert.Parameters.AddWithValue(type);
            insert.Parameters.Add(new NpgsqlParameter { Value = dataJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            await insert.ExecuteNonQueryAsync(ct);
        }

        await using (var update = new NpgsqlCommand(
            "update sections set summary = $2, estimated_minutes = $3, status = 'ready' where id = $1", conn, tx))
        {
            update.Parameters.AddWithValue(sectionId);
            update.Parameters.AddWithValue(summary);
            update.Parameters.AddWithValue(estimatedMinutes);
            await update.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<ExperienceRow?> GetLatestForSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select id, version, commit_sha, model, generated_at
            from experiences where session_id = $1 order by version desc limit 1
            """);
        cmd.Parameters.AddWithValue(sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new ExperienceRow(
            reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }

    public async Task<IReadOnlyList<SectionRow>> GetSectionsAsync(
        Guid experienceId, Guid userId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select sec.id, sec.slug, sec.kind, sec.title, sec.summary, sec.ord, sec.depth,
                   sec.parent_section_id, sec.estimated_minutes, sec.status,
                   coalesce(sp.state, 'unread'),
                   exists(select 1 from quizzes q where q.section_id = sec.id),
                   sp.quiz_best_pct
            from sections sec
            left join section_progress sp on sp.section_id = sec.id and sp.user_id = $2
            where sec.experience_id = $1
            order by sec.ord
            """);
        cmd.Parameters.AddWithValue(experienceId);
        cmd.Parameters.AddWithValue(userId);
        var rows = new List<SectionRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new SectionRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.GetInt32(8), reader.GetString(9), reader.GetString(10),
                reader.GetBoolean(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12)));
        }

        return rows;
    }

    /// <summary>Ownership-scoped section fetch: null unless the section's session belongs to the user.</summary>
    public async Task<SectionDetailRow?> GetSectionForUserAsync(
        Guid sectionId, Guid userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        SectionDetailRow? detail = null;
        await using (var cmd = new NpgsqlCommand(
            """
            select sec.id, sec.slug, sec.title, sec.kind, sec.status, q.id
            from sections sec
            join experiences e on e.id = sec.experience_id
            join sessions s on s.id = e.session_id
            left join quizzes q on q.section_id = sec.id
            where sec.id = $1 and s.user_id = $2
            """, conn))
        {
            cmd.Parameters.AddWithValue(sectionId);
            cmd.Parameters.AddWithValue(userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                detail = new SectionDetailRow(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5), []);
            }
        }

        if (detail is null)
        {
            return null;
        }

        var blocks = new List<BlockRow>();
        await using (var cmd = new NpgsqlCommand(
            "select id, ord, type, data::text from blocks where section_id = $1 order by ord", conn))
        {
            cmd.Parameters.AddWithValue(sectionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                blocks.Add(new BlockRow(
                    reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        return detail with { Blocks = blocks };
    }

    /// <summary>Upserts progress and returns (completed, total) over the session's depth-0 sections.</summary>
    public async Task<(int Completed, int Total)?> UpsertProgressAsync(
        Guid sectionId, Guid userId, string state, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        Guid experienceId;
        await using (var check = new NpgsqlCommand(
            """
            select sec.experience_id
            from sections sec
            join experiences e on e.id = sec.experience_id
            join sessions s on s.id = e.session_id
            where sec.id = $1 and s.user_id = $2
            """, conn))
        {
            check.Parameters.AddWithValue(sectionId);
            check.Parameters.AddWithValue(userId);
            if (await check.ExecuteScalarAsync(ct) is not Guid eid)
            {
                return null;
            }

            experienceId = eid;
        }

        await using (var upsert = new NpgsqlCommand(
            """
            insert into section_progress (user_id, section_id, state, updated_at)
            values ($1, $2, $3, now())
            on conflict (user_id, section_id) do update set state = excluded.state, updated_at = now()
            """, conn))
        {
            upsert.Parameters.AddWithValue(userId);
            upsert.Parameters.AddWithValue(sectionId);
            upsert.Parameters.AddWithValue(state);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using var counts = new NpgsqlCommand(
            """
            select count(*) filter (where sp.state = 'completed'), count(*)
            from sections sec
            left join section_progress sp on sp.section_id = sec.id and sp.user_id = $2
            where sec.experience_id = $1 and sec.depth = 0
            """, conn);
        counts.Parameters.AddWithValue(experienceId);
        counts.Parameters.AddWithValue(userId);
        await using var reader = await counts.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return ((int)reader.GetInt64(0), (int)reader.GetInt64(1));
    }

    /// <summary>Section title + concatenated markdown/callout text — the quiz generator's input.</summary>
    public async Task<(string Title, string Markdown)?> GetSectionTextAsync(Guid sectionId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        string? title = null;
        await using (var cmd = new NpgsqlCommand("select title from sections where id = $1", conn))
        {
            cmd.Parameters.AddWithValue(sectionId);
            title = await cmd.ExecuteScalarAsync(ct) as string;
        }

        if (title is null)
        {
            return null;
        }

        var parts = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            select type, data->>'md' from blocks
            where section_id = $1 and type in ('markdown','callout')
            order by ord
            """, conn))
        {
            cmd.Parameters.AddWithValue(sectionId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(1))
                {
                    parts.Add(reader.GetString(1));
                }
            }
        }

        return (title, string.Join("\n\n", parts));
    }

    public async Task<int> CountUnreadySectionsAsync(Guid experienceId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select count(*) from sections where experience_id = $1 and status <> 'ready'");
        cmd.Parameters.AddWithValue(experienceId);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
