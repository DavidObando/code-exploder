using System.Text.Json;
using CodeExploder.Domain;
using CodeExploder.Gateway.Contracts;
using CodeExploder.Storage;

namespace CodeExploder.Gateway;

/// <summary>
/// The generated tutorial experience: TOC, section content, and per-user progress
/// (docs/04-api.md). All routes resolve through the current user; other users'
/// sessions and sections 404.
/// </summary>
public static class ExperienceEndpoints
{
    private static readonly HashSet<string> ValidProgressStates =
        [ProgressState.Unread, ProgressState.Read, ProgressState.Skipped, ProgressState.Completed];

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sessions/{id:guid}/experience", async (
                Guid id, HttpContext http, SessionStore sessions, ExperienceStore store, CancellationToken ct) =>
            {
                var userId = await sessions.GetOrCreateUserAsync(
                    CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);
                if (await sessions.GetForUserAsync(id, userId, ct) is null)
                {
                    return Results.NotFound();
                }

                var experience = await store.GetLatestForSessionAsync(id, ct);
                if (experience is null)
                {
                    return Results.NotFound();
                }

                var sections = await store.GetSectionsAsync(experience.Id, userId, ct);
                return Results.Ok(new ExperienceToc(
                    experience.Id, experience.Version, experience.CommitSha, experience.Model,
                    experience.GeneratedAt,
                    sections.Select(s => new SectionTocEntry(
                        s.Id, s.Slug, s.Kind, s.Title, s.Summary, s.Ord, s.Depth,
                        s.ParentSectionId, s.EstimatedMinutes, s.Status, s.MyState,
                        s.HasQuiz, s.QuizBestPct)).ToList()));
            })
            .RequireAuthorization()
            .Produces<ExperienceToc>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapGet("/api/sections/{sectionId:guid}", async (
                Guid sectionId, HttpContext http, SessionStore sessions, ExperienceStore store, CancellationToken ct) =>
            {
                var userId = await sessions.GetOrCreateUserAsync(
                    CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);
                var section = await store.GetSectionForUserAsync(sectionId, userId, ct);
                return section is null
                    ? Results.NotFound()
                    : Results.Ok(new SectionDetail(
                        section.Id, section.Slug, section.Title, section.Kind, section.Status,
                        section.QuizId,
                        section.Blocks.Select(b => new BlockDto(
                            b.Id, b.Ord, b.Type, JsonSerializer.Deserialize<JsonElement>(b.DataJson))).ToList()));
            })
            .RequireAuthorization()
            .Produces<SectionDetail>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPut("/api/sections/{sectionId:guid}/progress", async (
                Guid sectionId, ProgressUpdateRequest body, HttpContext http,
                SessionStore sessions, ExperienceStore store, CancellationToken ct) =>
            {
                if (body.State is null || !ValidProgressStates.Contains(body.State))
                {
                    return Results.BadRequest(new ErrorResponse(
                        "state must be one of: unread, read, skipped, completed"));
                }

                var userId = await sessions.GetOrCreateUserAsync(
                    CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);
                var counts = await store.UpsertProgressAsync(sectionId, userId, body.State, ct);
                return counts is not { } c
                    ? Results.NotFound()
                    : Results.Ok(new ProgressUpdateResponse(
                        sectionId, body.State, new SessionProgress(c.Completed, c.Total)));
            })
            .RequireAuthorization()
            .Produces<ProgressUpdateResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);
    }
}
