using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Gateway.Contracts;
using CodeExploder.Storage;
using Npgsql;

namespace CodeExploder.Gateway;

/// <summary>
/// Q&A threads and messages (docs/04-api.md). POST message returns 202 — the answer
/// streams over the hub as transient QaToken events; the qa-answer job runs at
/// interactive priority on the gpu-gen lane.
/// </summary>
public static class QaEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sessions/{id:guid}/kb", async (
                Guid id, HttpContext http, SessionStore sessions, AnalysisStore analyses, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, sessions, ct);
                var session = await sessions.GetForUserAsync(id, userId, ct);
                if (session is null)
                {
                    return Results.NotFound();
                }

                var (embedded, total) = await analyses.EmbeddingCoverageAsync(session.AnalysisId, ct);
                return Results.Ok(new KbStatus(embedded, total, total > 0 && embedded == total));
            })
            .RequireAuthorization()
            .Produces<KbStatus>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/sessions/{id:guid}/threads", async (
                Guid id, HttpContext http, SessionStore sessions, QaStore qa, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, sessions, ct);
                if (await sessions.GetForUserAsync(id, userId, ct) is null)
                {
                    return Results.NotFound();
                }

                var threads = await qa.ListThreadsAsync(id, userId, ct);
                return Results.Ok(threads.Select(t =>
                    new ThreadDto(t.Id, t.Title, t.CreatedAt, t.LastMessageAt)).ToList());
            })
            .RequireAuthorization()
            .Produces<IReadOnlyList<ThreadDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/sessions/{id:guid}/threads", async (
                Guid id, NewThreadRequest body, HttpContext http, SessionStore sessions, QaStore qa,
                CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, sessions, ct);
                if (await sessions.GetForUserAsync(id, userId, ct) is null)
                {
                    return Results.NotFound();
                }

                var title = string.IsNullOrWhiteSpace(body.Title) ? "Ask the expert" : body.Title.Trim();
                var threadId = await qa.CreateThreadAsync(id, userId, title, ct);
                return Results.Created(
                    $"/api/threads/{threadId}",
                    new ThreadDto(threadId, title, DateTimeOffset.UtcNow, null));
            })
            .RequireAuthorization()
            .Produces<ThreadDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/threads/{threadId:guid}/messages", async (
                Guid threadId, HttpContext http, SessionStore sessions, QaStore qa, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, sessions, ct);
                if (await qa.GetThreadContextAsync(threadId, userId, ct) is null)
                {
                    return Results.NotFound();
                }

                var messages = await qa.ListMessagesAsync(threadId, ct);
                return Results.Ok(messages.Select(ToDto).ToList());
            })
            .RequireAuthorization()
            .Produces<IReadOnlyList<QaMessageDto>>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/threads/{threadId:guid}/messages", async (
                Guid threadId, NewMessageRequest body, HttpContext http,
                SessionStore sessions, QaStore qa, JobQueue queue, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(body.Content))
                {
                    return Results.BadRequest(new ErrorResponse("content is required"));
                }

                var userId = await ResolveUserAsync(http, sessions, ct);
                var context = await qa.GetThreadContextAsync(threadId, userId, ct);
                if (context is not { } threadContext)
                {
                    return Results.NotFound();
                }

                if (await qa.HasStreamingMessageAsync(threadId, ct))
                {
                    return Results.Conflict(new ErrorResponse("The expert is still answering — wait or stop it first."));
                }

                var (userMessageId, assistantMessageId) = await qa.AppendExchangeAsync(
                    threadId, body.Content.Trim(), body.SectionContext, ct);

                await queue.EnqueueAsync(
                    LlmJobTypes.QaAnswer,
                    JsonSerializer.Serialize(new
                    {
                        analysisId = threadContext.AnalysisId,
                        sessionId = threadContext.SessionId,
                        threadId,
                        messageId = assistantMessageId,
                    }, JsonOpts),
                    priority: LlmJobTypes.InteractivePriority,
                    analysisId: threadContext.AnalysisId,
                    ct: ct);

                return Results.Accepted(value: new NewMessageResponse(userMessageId, assistantMessageId));
            })
            .RequireAuthorization()
            .Produces<NewMessageResponse>(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/messages/{messageId:guid}/cancel", async (
                Guid messageId, HttpContext http, SessionStore sessions, QaStore qa, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, sessions, ct);
                return await qa.RequestCancelAsync(messageId, userId, ct)
                    ? Results.Accepted()
                    : Results.NotFound();
            })
            .RequireAuthorization()
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/analyses/{analysisId:guid}/chunks/{chunkId:guid}", async (
                Guid analysisId, Guid chunkId, HttpContext http, SessionStore sessions,
                NpgsqlDataSource db, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, sessions, ct);
                await using var cmd = db.CreateCommand(
                    """
                    select f.path, c.start_line, c.end_line, f.language, c.content
                    from chunks c
                    join files f on f.id = c.file_id
                    where c.id = $1 and c.analysis_id = $2
                      and exists (select 1 from sessions s where s.analysis_id = $2 and s.user_id = $3)
                    """);
                cmd.Parameters.AddWithValue(chunkId);
                cmd.Parameters.AddWithValue(analysisId);
                cmd.Parameters.AddWithValue(userId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                return await reader.ReadAsync(ct)
                    ? Results.Ok(new ChunkPeek(
                        reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                        reader.GetString(3), reader.GetString(4)))
                    : Results.NotFound();
            })
            .RequireAuthorization()
            .Produces<ChunkPeek>()
            .Produces(StatusCodes.Status404NotFound);
    }

    private static Task<Guid> ResolveUserAsync(HttpContext http, SessionStore sessions, CancellationToken ct) =>
        sessions.GetOrCreateUserAsync(CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);

    private static QaMessageDto ToDto(QaMessageRow m) => new(
        m.Id, m.Ord, m.Role, m.Content, m.Status,
        m.CitationsJson is null ? null : JsonSerializer.Deserialize<JsonElement>(m.CitationsJson),
        m.CreatedAt);
}
