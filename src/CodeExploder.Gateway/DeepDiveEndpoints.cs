using CodeExploder.Domain;
using CodeExploder.Gateway.Contracts;
using CodeExploder.Storage;

namespace CodeExploder.Gateway;

/// <summary>
/// M10 deep dives (recursive scope explosion): list explodable scopes, start a dive
/// on demand, retry a failed one. All routes resolve through the current user; other
/// users' sessions 404. A dive never touches the session's own status.
/// </summary>
public static class DeepDiveEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sessions/{id:guid}/scopes", async (
                Guid id, Guid? parentComponentId, HttpContext http,
                SessionStore sessions, AnalysisStore analyses, ExperienceStore experiences,
                ExplosionStore explosions, ExplosionOptions options, CancellationToken ct) =>
            {
                var userId = await sessions.GetOrCreateUserAsync(
                    CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);
                var session = await sessions.GetForUserAsync(id, userId, ct);
                if (session is null)
                {
                    return Results.NotFound();
                }

                var experience = await experiences.GetLatestForSessionAsync(id, ct);
                if (experience is null)
                {
                    return Results.Ok(new ScopeList([]));
                }

                IReadOnlyList<ComponentRow> components;
                if (parentComponentId is { } parentId)
                {
                    var parent = await analyses.GetComponentAsync(parentId, ct);
                    if (parent is null || parent.AnalysisId != session.AnalysisId)
                    {
                        return Results.NotFound();
                    }

                    components = await analyses.GetSubComponentsAsync(parentId, ct);
                }
                else
                {
                    components = await analyses.GetTopLevelComponentsAsync(session.AnalysisId, ct);
                }

                var byComponent = (await explosions.ListForExperienceAsync(experience.Id, ct))
                    .ToDictionary(e => e.ComponentId);
                var scopes = components.Select(c =>
                {
                    byComponent.TryGetValue(c.Id, out var explosion);
                    var explodable = c.FileCount >= options.MinScopeFiles && c.Depth + 1 <= options.MaxDepth;
                    return new ScopeInfo(
                        c.Id, c.Name, c.FileCount, c.Depth, explodable,
                        explosion is null
                            ? null
                            : new ScopeExplosionInfo(
                                explosion.Id, explosion.Status, explosion.Trigger,
                                explosion.SectionId, explosion.SectionsReady, explosion.SectionsTotal));
                }).ToList();
                return Results.Ok(new ScopeList(scopes));
            })
            .RequireAuthorization()
            .Produces<ScopeList>()
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/sessions/{id:guid}/explode", async (
                Guid id, ExplodeRequest body, HttpContext http,
                SessionStore sessions, AnalysisStore analyses, ExperienceStore experiences,
                ExplosionStore explosions, ExplosionLauncher launcher, ExplosionOptions options,
                CancellationToken ct) =>
            {
                var userId = await sessions.GetOrCreateUserAsync(
                    CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);
                var session = await sessions.GetForUserAsync(id, userId, ct);
                if (session is null)
                {
                    return Results.NotFound();
                }

                if (session.Kind != SessionKind.Repo)
                {
                    return Results.BadRequest(new ErrorResponse("Deep dives are for repository sessions."));
                }

                if (session.Status is not (SessionStatus.Ready or SessionStatus.Partial))
                {
                    return Results.Conflict(new ErrorResponse("Wait for the analysis to finish first."));
                }

                var experience = await experiences.GetLatestForSessionAsync(id, ct);
                if (experience is null)
                {
                    return Results.NotFound();
                }

                var component = await analyses.GetComponentAsync(body.ComponentId, ct);
                if (component is null || component.AnalysisId != session.AnalysisId)
                {
                    return Results.NotFound();
                }

                var explosionDepth = component.Depth + 1;
                if (explosionDepth > options.MaxDepth)
                {
                    return Results.BadRequest(new ErrorResponse(
                        $"This scope is already {options.MaxDepth} levels deep — that's the floor."));
                }

                if (component.FileCount < options.MinScopeFiles)
                {
                    return Results.BadRequest(new ErrorResponse(
                        "This scope is too small to be worth a deep dive."));
                }

                // Resolve the anchor: top-level dives hang under the architecture
                // section; nested dives hang under their parent's deep-dive section.
                Guid? parentExplosionId = null;
                Guid? anchorSectionId;
                var sectionDepth = 1;
                if (component.ParentComponentId is { } parentComponentId)
                {
                    var parentExplosion = await explosions.GetByComponentAsync(experience.Id, parentComponentId, ct);
                    if (parentExplosion?.SectionId is null)
                    {
                        return Results.Conflict(new ErrorResponse("Explode the parent scope first."));
                    }

                    parentExplosionId = parentExplosion.Id;
                    anchorSectionId = parentExplosion.SectionId;
                    var parentMeta = await experiences.GetSectionMetaAsync(parentExplosion.SectionId.Value, ct);
                    sectionDepth = (parentMeta?.Depth ?? 0) + 1;
                }
                else
                {
                    anchorSectionId = await experiences.GetSectionIdBySlugAsync(experience.Id, "architecture", ct);
                }

                // The concurrency cap only gates NEW dives — a duplicate POST must
                // stay idempotent even at the cap.
                var existing = await explosions.GetByComponentAsync(experience.Id, body.ComponentId, ct);
                if (existing is null
                    && await explosions.CountActiveForAnalysisAsync(session.AnalysisId, ct) >= options.MaxActivePerAnalysis)
                {
                    return Results.Conflict(new ErrorResponse(
                        "Another deep dive is already in progress — let it land first."));
                }

                var launch = await launcher.LaunchAsync(new ExplosionRequest(
                    session.AnalysisId, id, experience.Id,
                    component.Id, component.Name,
                    explosionDepth, parentExplosionId, anchorSectionId, sectionDepth,
                    ExplosionTrigger.OnDemand,
                    session.RepoOwner, session.RepoName, null), ct);

                var response = new ExplodeResponse(
                    launch.Explosion.Id, launch.Explosion.SectionId, launch.Explosion.Status);
                return launch.Created
                    ? Results.Accepted(value: response)
                    : Results.Ok(response);
            })
            .RequireAuthorization()
            .Produces<ExplodeResponse>(StatusCodes.Status202Accepted)
            .Produces<ExplodeResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/explosions/{explosionId:guid}/retry", async (
                Guid explosionId, HttpContext http,
                SessionStore sessions, AnalysisStore analyses, ExperienceStore experiences,
                ExplosionStore explosions, ExplosionLauncher launcher, CancellationToken ct) =>
            {
                var userId = await sessions.GetOrCreateUserAsync(
                    CurrentUser.SubjectOf(http.User), CurrentUser.NameOf(http.User), ct);
                var row = await explosions.GetAsync(explosionId, ct);
                var sessionId = row is null ? null : await explosions.GetSessionIdAsync(explosionId, ct);
                var session = sessionId is { } sid ? await sessions.GetForUserAsync(sid, userId, ct) : null;
                if (row is null || session is null)
                {
                    return Results.NotFound();
                }

                // Re-checked under lock: only a failed dive relaunches, exactly once.
                if (!await explosions.ResetForRetryAsync(explosionId, ExplosionTrigger.OnDemand, ct))
                {
                    return Results.Conflict(new ErrorResponse("Only a failed deep dive can be retried."));
                }

                var component = await analyses.GetComponentAsync(row.ComponentId, ct)
                    ?? throw new InvalidOperationException("explosion component row missing");
                var sectionDepth = row.SectionId is { } sec
                    ? (await experiences.GetSectionMetaAsync(sec, ct))?.Depth ?? row.Depth
                    : row.Depth;
                var fresh = await explosions.GetAsync(explosionId, ct) ?? row;
                await launcher.RelaunchAsync(new ExplosionRequest(
                    row.AnalysisId, session.Id, row.ExperienceId,
                    component.Id, component.Name,
                    row.Depth, row.ParentExplosionId, null, sectionDepth,
                    ExplosionTrigger.OnDemand,
                    session.RepoOwner, session.RepoName, null), fresh, ct);

                return Results.Accepted(value: new ExplodeResponse(explosionId, fresh.SectionId, ExplosionStatus.Queued));
            })
            .RequireAuthorization()
            .Produces<ExplodeResponse>(StatusCodes.Status202Accepted)
            .Produces<ErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound);
    }
}
