using System.Text;
using CodeExploder.Domain;
using CodeExploder.Gateway.Export;
using CodeExploder.Storage;

namespace CodeExploder.Gateway;

/// <summary>
/// Offline static export: `GET /api/sessions/{id}/export.html` returns the reading
/// tour as a single self-contained HTML file (the server twin of the in-app Download
/// button), so an analysis can be pulled via curl/MCP without opening the SPA. Behind
/// the same auth as every session route.
/// </summary>
public static class ExportEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sessions/{id:guid}/export.html", async (
                Guid id, HttpContext http, SessionStore sessions, ExperienceStore experiences,
                CancellationToken ct) =>
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
                    return Results.NotFound();
                }

                var allSections = await experiences.GetSectionsAsync(experience.Id, userId, ct);
                var ready = allSections.Where(s => s.Status == SectionState.Ready).ToList();
                if (ready.Count == 0)
                {
                    return Results.NotFound(new Contracts.ErrorResponse("Nothing to export yet — no sections are ready."));
                }

                var blocksBySection = (await experiences.GetBlocksForExperienceAsync(experience.Id, ct))
                    .GroupBy(b => b.SectionId)
                    .ToDictionary(
                        g => g.Key,
                        g => (IReadOnlyList<StaticExportRenderer.ExportBlock>)g
                            .OrderBy(b => b.Block.Ord)
                            .Select(b => new StaticExportRenderer.ExportBlock(b.Block.Type, b.Block.DataJson))
                            .ToList());

                var ordered = TreeOrder(ready)
                    .Select(s => new StaticExportRenderer.ExportSection(
                        s.Slug, s.Title, s.Kind, s.Depth,
                        blocksBySection.GetValueOrDefault(s.Id, [])))
                    .ToList();

                var html = StaticExportRenderer.Render(
                    new StaticExportRenderer.ExportMeta(
                        session.Title, session.RepoOwner, session.RepoName,
                        experience.CommitSha, DateTimeOffset.UtcNow),
                    ordered,
                    new StaticExportRenderer.GithubContext(session.RepoOwner, session.RepoName, experience.CommitSha));

                return Results.File(
                    Encoding.UTF8.GetBytes(html),
                    "text/html; charset=utf-8",
                    StaticExportRenderer.FileName(session.RepoOwner, session.RepoName));
            })
            .RequireAuthorization()
            .Produces(StatusCodes.Status200OK, contentType: "text/html")
            .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>Depth-first reading order: siblings by ord, orphans (parent not ready)
    /// treated as roots — matches the client's buildTocTree/flattenAll.</summary>
    private static List<SectionRow> TreeOrder(List<SectionRow> ready)
    {
        var ids = ready.Select(s => s.Id).ToHashSet();
        var childrenByParent = ready
            .Where(s => s.ParentSectionId is { } p && ids.Contains(p))
            .GroupBy(s => s.ParentSectionId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Ord).ToList());
        var roots = ready
            .Where(s => s.ParentSectionId is not { } p || !ids.Contains(p))
            .OrderBy(s => s.Ord);

        var result = new List<SectionRow>(ready.Count);
        void Visit(SectionRow s)
        {
            result.Add(s);
            if (childrenByParent.TryGetValue(s.Id, out var kids))
            {
                foreach (var kid in kids)
                {
                    Visit(kid);
                }
            }
        }

        foreach (var root in roots)
        {
            Visit(root);
        }

        return result;
    }
}
