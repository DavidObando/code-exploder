using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Gateway.Contracts;
using CodeExploder.Storage;
using Npgsql;

namespace CodeExploder.Gateway;

/// <summary>
/// Session CRUD + analysis-progress snapshot (docs/04-api.md). All session-scoped
/// routes resolve through the current user and 404 on other users' sessions.
/// </summary>
public static class SessionEndpoints
{
    /// <summary>The pipeline's first job (S0 acquire, docs/01-analysis-pipeline.md).</summary>
    public const string AcquireJob = "acquire";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sessions", async (HttpContext http, SessionStore store, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, store, ct);
                var sessions = await store.ListForUserAsync(userId, ct);
                return Results.Ok(sessions.Select(ToSummary).ToList());
            })
            .RequireAuthorization()
            .Produces<IReadOnlyList<SessionSummary>>();

        app.MapPost("/api/sessions", async (
                NewSessionRequest body,
                HttpContext http,
                SessionStore store,
                JobQueue queue,
                CancellationToken ct) =>
            {
                if (!GitHubUrl.TryParse(body.Url, out var url))
                {
                    return Results.BadRequest(new ErrorResponse(
                        "Enter a public GitHub repository or pull request URL (https://github.com/owner/repo)."));
                }

                var userId = await ResolveUserAsync(http, store, ct);
                var repoId = await store.GetOrCreateRepoAsync(url.Owner, url.Name, url.CanonicalUrl, ct);
                var analysisId = await store.CreateAnalysisAsync(repoId, url.Kind, url.PrNumber, ct);
                var sessionId = await store.CreateSessionAsync(userId, analysisId, url.Kind, url.Title, ct);

                await queue.EnqueueAsync(
                    AcquireJob,
                    JsonSerializer.Serialize(new
                    {
                        analysisId,
                        sessionId,
                        owner = url.Owner,
                        name = url.Name,
                        prNumber = url.PrNumber,
                        gitRef = string.IsNullOrWhiteSpace(body.GitRef) ? null : body.GitRef.Trim(),
                    }),
                    analysisId: analysisId,
                    ct: ct);

                var session = await store.GetForUserAsync(sessionId, userId, ct);
                return Results.Created($"/api/sessions/{sessionId}", ToSummary(session!));
            })
            .RequireAuthorization()
            .Produces<SessionSummary>(StatusCodes.Status201Created)
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

        app.MapGet("/api/sessions/{id:guid}", async (
                Guid id, HttpContext http, SessionStore store, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, store, ct);
                var session = await store.GetForUserAsync(id, userId, ct);
                return session is null ? Results.NotFound() : Results.Ok(ToSummary(session));
            })
            .RequireAuthorization()
            .Produces<SessionSummary>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapDelete("/api/sessions/{id:guid}", async (
                Guid id, HttpContext http, SessionStore store, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, store, ct);
                if (await store.GetForUserAsync(id, userId, ct) is null)
                {
                    return Results.NotFound();
                }

                await store.DeleteAsync(id, ct);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/sessions/{id:guid}/analysis", async (
                Guid id, HttpContext http, SessionStore store, AnalysisStore analyses,
                NpgsqlDataSource db, CancellationToken ct) =>
            {
                var userId = await ResolveUserAsync(http, store, ct);
                var session = await store.GetForUserAsync(id, userId, ct);
                if (session is null)
                {
                    return Results.NotFound();
                }

                RepoSummary? summary = null;
                if (session.Status is SessionStatus.Ready or SessionStatus.Partial
                    && await analyses.GetPlanAsync(session.AnalysisId, ct) is { } planJson)
                {
                    summary = JsonSerializer.Deserialize<PlanDocument>(planJson, PlanJsonOpts)?.Summary;
                }

                return Results.Ok(await BuildSnapshotAsync(session.Status, id, summary, db, ct));
            })
            .RequireAuthorization()
            .Produces<AnalysisSnapshot>()
            .Produces(StatusCodes.Status404NotFound);
    }

    private static Task<Guid> ResolveUserAsync(HttpContext http, SessionStore store, CancellationToken ct) =>
        store.GetOrCreateUserAsync(CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);

    private static SessionSummary ToSummary(SessionView s) => new(
        s.Id, s.Kind, s.Title, s.RepoOwner, s.RepoName, s.PrNumber, s.Status, s.FailureReason,
        s.CreatedAt, new SessionProgress(s.SectionsReady, s.SectionsTotal), s.AnalysisId);

    /// <summary>
    /// Mount snapshot for the progress view: folds the durable event log into per-stage
    /// state so a page load agrees exactly with what live listeners saw. lastEventId
    /// seeds the hub's GetEventsSince catch-up.
    /// </summary>
    private static readonly JsonSerializerOptions PlanJsonOpts = new(JsonSerializerDefaults.Web);

    private sealed record PlanDocument(RepoSummary? Summary);

    private static async Task<AnalysisSnapshot> BuildSnapshotAsync(
        string sessionStatus, Guid sessionId, RepoSummary? summary, NpgsqlDataSource db, CancellationToken ct)
    {
        var stages = AnalysisStages.All.ToDictionary(
            s => s.Key,
            s => new StageInfo(s.Key, s.Label, StageState.Pending, null, null));
        var narration = new List<NarrationLine>();
        long lastEventId = 0;

        await using var cmd = db.CreateCommand(
            "select id, payload::text from pipeline_events where session_id = $1 order by id");
        cmd.Parameters.AddWithValue(sessionId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            lastEventId = reader.GetInt64(0);
            using var doc = JsonDocument.Parse(reader.GetString(1));
            var root = doc.RootElement;
            var kind = root.GetProperty("kind").GetString();
            var at = root.GetProperty("at").GetDateTimeOffset();
            var data = root.GetProperty("data");

            switch (kind)
            {
                case SessionEventKinds.AnalysisStageChanged:
                {
                    var stage = data.GetProperty("stage").GetString()!;
                    var state = data.GetProperty("state").GetString()!;
                    if (stages.TryGetValue(stage, out var info))
                    {
                        stages[stage] = info with { State = state, Percent = state == StageState.Done ? 100 : info.Percent };
                    }

                    break;
                }

                case SessionEventKinds.AnalysisProgress:
                {
                    var stage = data.GetProperty("stage").GetString()!;
                    if (stages.TryGetValue(stage, out var info))
                    {
                        stages[stage] = info with
                        {
                            Percent = data.GetProperty("percent").GetDouble(),
                            Detail = data.TryGetProperty("detail", out var d) ? d.GetString() : info.Detail,
                        };
                    }

                    break;
                }

                case SessionEventKinds.AnalysisNarration:
                    narration.Add(new NarrationLine(at, data.GetProperty("text").GetString()!));
                    break;

                default:
                    break;
            }
        }

        return new AnalysisSnapshot(
            sessionStatus,
            AnalysisStages.All.Select(s => stages[s.Key]).ToList(),
            narration,
            lastEventId,
            summary);
    }
}
