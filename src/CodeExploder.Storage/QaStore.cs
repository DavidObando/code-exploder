using Npgsql;
using NpgsqlTypes;

namespace CodeExploder.Storage;

public sealed record QaThreadRow(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset? LastMessageAt);

public sealed record QaMessageRow(
    Guid Id, Guid ThreadId, int Ord, string Role, string Content, string Status,
    string? CitationsJson, Guid? SectionContext, DateTimeOffset CreatedAt);

/// <summary>Raw-SQL store for Q&A threads and messages (docs/03).</summary>
public sealed class QaStore(NpgsqlDataSource dataSource)
{
    public async Task<Guid> CreateThreadAsync(
        Guid sessionId, Guid userId, string title, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            "insert into qa_threads (id, session_id, user_id, title) values ($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(title);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<QaThreadRow>> ListThreadsAsync(
        Guid sessionId, Guid userId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select t.id, t.title, t.created_at,
                   (select max(m.created_at) from qa_messages m where m.thread_id = t.id)
            from qa_threads t
            where t.session_id = $1 and t.user_id = $2
            order by t.created_at desc
            """);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(userId);
        var rows = new List<QaThreadRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new QaThreadRow(
                reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2),
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }

        return rows;
    }

    /// <summary>Ownership-scoped: (sessionId, analysisId) of the thread, null if not the user's.</summary>
    public async Task<(Guid SessionId, Guid AnalysisId)?> GetThreadContextAsync(
        Guid threadId, Guid userId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select s.id, s.analysis_id
            from qa_threads t
            join sessions s on s.id = t.session_id
            where t.id = $1 and t.user_id = $2
            """);
        cmd.Parameters.AddWithValue(threadId);
        cmd.Parameters.AddWithValue(userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetGuid(0), reader.GetGuid(1)) : null;
    }

    public async Task<IReadOnlyList<QaMessageRow>> ListMessagesAsync(Guid threadId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select id, thread_id, ord, role, content, status, citations::text, section_context, created_at
            from qa_messages where thread_id = $1 order by ord
            """);
        cmd.Parameters.AddWithValue(threadId);
        var rows = new List<QaMessageRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadMessage(reader));
        }

        return rows;
    }

    public async Task<bool> HasStreamingMessageAsync(Guid threadId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "select 1 from qa_messages where thread_id = $1 and status = 'streaming' limit 1");
        cmd.Parameters.AddWithValue(threadId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Appends the user message + the empty streaming assistant message atomically.</summary>
    public async Task<(Guid UserMessageId, Guid AssistantMessageId)> AppendExchangeAsync(
        Guid threadId, string content, Guid? sectionContext, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        int nextOrd;
        await using (var ord = new NpgsqlCommand(
            "select coalesce(max(ord), -1) + 1 from qa_messages where thread_id = $1", conn, tx))
        {
            ord.Parameters.AddWithValue(threadId);
            nextOrd = (int)(await ord.ExecuteScalarAsync(ct))!;
        }

        var userId = Guid.NewGuid();
        var assistantId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            """
            insert into qa_messages (id, thread_id, ord, role, content, status, section_context) values
            ($1, $3, $4, 'user', $5, 'complete', $6),
            ($2, $3, $4 + 1, 'assistant', '', 'streaming', $6)
            """, conn, tx))
        {
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(assistantId);
            insert.Parameters.AddWithValue(threadId);
            insert.Parameters.AddWithValue(nextOrd);
            insert.Parameters.AddWithValue(content);
            insert.Parameters.Add(new NpgsqlParameter
            {
                Value = (object?)sectionContext ?? DBNull.Value,
                NpgsqlDbType = NpgsqlDbType.Uuid,
            });
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return (userId, assistantId);
    }

    public async Task<QaMessageRow?> GetMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select id, thread_id, ord, role, content, status, citations::text, section_context, created_at
            from qa_messages where id = $1
            """);
        cmd.Parameters.AddWithValue(messageId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadMessage(reader) : null;
    }

    /// <summary>The ~1/s partial flush while streaming (reconnect fallback, docs/04).</summary>
    public async Task UpdatePartialContentAsync(Guid messageId, string content, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update qa_messages set content = $2 where id = $1 and status = 'streaming'");
        cmd.Parameters.AddWithValue(messageId);
        cmd.Parameters.AddWithValue(content);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CompleteMessageAsync(
        Guid messageId, string status, string content, string? citationsJson,
        int? promptTokens, int? completionTokens, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            update qa_messages
            set status = $2, content = $3, citations = $4, prompt_tokens = $5, completion_tokens = $6
            where id = $1
            """);
        cmd.Parameters.AddWithValue(messageId);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(content);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)citationsJson ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.AddWithValue((object?)promptTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)completionTokens ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Cancellation flag: the worker polls status between flushes.</summary>
    public async Task<bool> RequestCancelAsync(Guid messageId, Guid userId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            update qa_messages m
            set status = 'cancelled'
            from qa_threads t
            where m.id = $1 and m.thread_id = t.id and t.user_id = $2 and m.status = 'streaming'
            """);
        cmd.Parameters.AddWithValue(messageId);
        cmd.Parameters.AddWithValue(userId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<string?> GetStatusAsync(Guid messageId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("select status from qa_messages where id = $1");
        cmd.Parameters.AddWithValue(messageId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static QaMessageRow ReadMessage(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetInt32(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7),
        reader.GetFieldValue<DateTimeOffset>(8));
}
