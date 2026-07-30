using Npgsql;
using NpgsqlTypes;

namespace CodeExploder.Storage;

public sealed record QuizQuestionRow(Guid Id, int Ord, string Type, string Prompt, string DataJson);

public sealed record QuizRow(Guid Id, Guid SectionId, string Title, IReadOnlyList<QuizQuestionRow> Questions);

public sealed record QuizAttemptRow(
    Guid Id, Guid QuizId, Guid UserId, string AnswersJson, DateTimeOffset SubmittedAt,
    string Status, int? ScorePct, string? FeedbackJson);

/// <summary>
/// Raw-SQL store for quizzes (docs/03). Answer keys/rubrics stay in
/// quiz_questions.data — the Gateway strips them before serving clients.
/// </summary>
public sealed class QuizStore(NpgsqlDataSource dataSource)
{
    public async Task<Guid> CreateQuizAsync(
        Guid sectionId, string title,
        IReadOnlyList<(string Type, string Prompt, string DataJson)> questions,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Regeneration replaces: at most one quiz per section.
        await using (var clear = new NpgsqlCommand("delete from quizzes where section_id = $1", conn, tx))
        {
            clear.Parameters.AddWithValue(sectionId);
            await clear.ExecuteNonQueryAsync(ct);
        }

        var quizId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            "insert into quizzes (id, section_id, title) values ($1, $2, $3)", conn, tx))
        {
            insert.Parameters.AddWithValue(quizId);
            insert.Parameters.AddWithValue(sectionId);
            insert.Parameters.AddWithValue(title);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var ord = 0;
        foreach (var (type, prompt, dataJson) in questions)
        {
            await using var q = new NpgsqlCommand(
                "insert into quiz_questions (id, quiz_id, ord, type, prompt, data) values ($1, $2, $3, $4, $5, $6)",
                conn, tx);
            q.Parameters.AddWithValue(Guid.NewGuid());
            q.Parameters.AddWithValue(quizId);
            q.Parameters.AddWithValue(ord++);
            q.Parameters.AddWithValue(type);
            q.Parameters.AddWithValue(prompt);
            q.Parameters.Add(new NpgsqlParameter { Value = dataJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            await q.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return quizId;
    }

    /// <summary>Ownership-scoped quiz fetch (full data — caller strips keys for clients).</summary>
    public async Task<QuizRow?> GetForUserAsync(Guid quizId, Guid userId, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        QuizRow? quiz = null;
        await using (var cmd = new NpgsqlCommand(
            """
            select q.id, q.section_id, q.title
            from quizzes q
            join sections sec on sec.id = q.section_id
            join experiences e on e.id = sec.experience_id
            join sessions s on s.id = e.session_id
            where q.id = $1 and s.user_id = $2
            """, conn))
        {
            cmd.Parameters.AddWithValue(quizId);
            cmd.Parameters.AddWithValue(userId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                quiz = new QuizRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), []);
            }
        }

        if (quiz is null)
        {
            return null;
        }

        var questions = new List<QuizQuestionRow>();
        await using (var cmd = new NpgsqlCommand(
            "select id, ord, type, prompt, data::text from quiz_questions where quiz_id = $1 order by ord", conn))
        {
            cmd.Parameters.AddWithValue(quizId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                questions.Add(new QuizQuestionRow(
                    reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4)));
            }
        }

        return quiz with { Questions = questions };
    }

    public async Task<Guid?> GetQuizIdForSectionAsync(Guid sectionId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("select id from quizzes where section_id = $1");
        cmd.Parameters.AddWithValue(sectionId);
        return await cmd.ExecuteScalarAsync(ct) as Guid?;
    }

    public async Task<Guid> CreateAttemptAsync(
        Guid quizId, Guid userId, string answersJson, string status, int? scorePct, string? feedbackJson,
        CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var cmd = dataSource.CreateCommand(
            """
            insert into quiz_attempts (id, quiz_id, user_id, answers, status, score_pct, feedback)
            values ($1, $2, $3, $4::jsonb, $5, $6, $7)
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(quizId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(answersJson);
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue((object?)scorePct ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = (object?)feedbackJson ?? DBNull.Value,
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task CompleteGradingAsync(
        Guid attemptId, int scorePct, string feedbackJson, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "update quiz_attempts set status = 'graded', score_pct = $2, feedback = $3::jsonb where id = $1");
        cmd.Parameters.AddWithValue(attemptId);
        cmd.Parameters.AddWithValue(scorePct);
        cmd.Parameters.AddWithValue(feedbackJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<QuizAttemptRow?> GetAttemptAsync(Guid attemptId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select id, quiz_id, user_id, answers::text, submitted_at, status, score_pct, feedback::text
            from quiz_attempts where id = $1
            """);
        cmd.Parameters.AddWithValue(attemptId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAttempt(reader) : null;
    }

    public async Task<IReadOnlyList<QuizAttemptRow>> GetAttemptsAsync(
        Guid quizId, Guid userId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select id, quiz_id, user_id, answers::text, submitted_at, status, score_pct, feedback::text
            from quiz_attempts where quiz_id = $1 and user_id = $2 order by submitted_at desc limit 20
            """);
        cmd.Parameters.AddWithValue(quizId);
        cmd.Parameters.AddWithValue(userId);
        var rows = new List<QuizAttemptRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(ReadAttempt(reader));
        }

        return rows;
    }

    /// <summary>
    /// Records the best score and auto-completes the section at ≥75 % (docs/05 §Quizzes) —
    /// completion is an upgrade only, never a downgrade of an existing state.
    /// </summary>
    public async Task RecordScoreAsync(
        Guid quizId, Guid userId, int scorePct, int completeThreshold, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            insert into section_progress (user_id, section_id, state, quiz_best_pct, updated_at)
            select $2, q.section_id,
                   case when $3 >= $4 then 'completed' else 'unread' end,
                   $3, now()
            from quizzes q where q.id = $1
            on conflict (user_id, section_id) do update set
                quiz_best_pct = greatest(coalesce(section_progress.quiz_best_pct, 0), excluded.quiz_best_pct),
                state = case
                    when $3 >= $4 then 'completed'
                    else section_progress.state
                end,
                updated_at = now()
            """);
        cmd.Parameters.AddWithValue(quizId);
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(scorePct);
        cmd.Parameters.AddWithValue(completeThreshold);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Resolves the session id owning a quiz — event routing needs it.</summary>
    public async Task<(Guid SessionId, Guid SectionId)?> GetSessionForQuizAsync(Guid quizId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            select e.session_id, q.section_id
            from quizzes q
            join sections sec on sec.id = q.section_id
            join experiences e on e.id = sec.experience_id
            where q.id = $1
            """);
        cmd.Parameters.AddWithValue(quizId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetGuid(0), reader.GetGuid(1)) : null;
    }

    private static QuizAttemptRow ReadAttempt(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
        reader.GetFieldValue<DateTimeOffset>(4), reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));
}
