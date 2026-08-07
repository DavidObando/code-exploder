using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Storage.Tests;

/// <summary>
/// The explosion contract (M10): one row per (experience, component) with the loser of
/// a create race told so; retry only from failed; active-count guards concurrency; the
/// analysis cascade sweeps explosions away; sub-components stay out of top-level reads.
/// </summary>
public sealed class ExplosionStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private ExplosionStore Explosions => new(fixture.DataSource);

    private async Task<(Guid AnalysisId, Guid ExperienceId, Guid ComponentId)> SeedAsync(string marker)
    {
        var sessions = new SessionStore(fixture.DataSource);
        var userId = await sessions.GetOrCreateUserAsync($"explosion-{marker}", "Explosion Tests");
        var repoId = await sessions.GetOrCreateRepoAsync("octo", $"explode-{marker}", $"https://github.com/octo/explode-{marker}");
        var analysisId = await sessions.CreateAnalysisAsync(repoId, "repo", null);
        var sessionId = await sessions.CreateSessionAsync(userId, analysisId, "repo", $"octo/explode-{marker}");

        var analysis = new AnalysisStore(fixture.DataSource);
        await analysis.InsertComponentsAsync(analysisId,
            [new Component("core", ["src/core"], ["src/core/a.cs"], ["src/core/a.cs"])]);
        var componentId = (await analysis.GetComponentsAsync(analysisId))[0].Id;

        var experiences = new ExperienceStore(fixture.DataSource);
        var experienceId = await experiences.CreateExperienceAsync(sessionId, "abc123", "test-model");
        return (analysisId, experienceId, componentId);
    }

    [Fact]
    public async Task CreateIsUniquePerExperienceAndComponent()
    {
        var (analysisId, experienceId, componentId) = await SeedAsync("unique");
        var store = Explosions;

        var first = await store.TryCreateAsync(analysisId, experienceId, componentId, null, 1, "on_demand");
        var second = await store.TryCreateAsync(analysisId, experienceId, componentId, null, 1, "eager");

        Assert.NotNull(first);
        Assert.Null(second);
        var row = await store.GetByComponentAsync(experienceId, componentId);
        Assert.NotNull(row);
        Assert.Equal(first, row.Id);
        Assert.Equal("on_demand", row.Trigger);
        Assert.Equal("queued", row.Status);
    }

    [Fact]
    public async Task ResetForRetryOnlyFromFailed()
    {
        var (analysisId, experienceId, componentId) = await SeedAsync("retry");
        var store = Explosions;
        var id = (await store.TryCreateAsync(analysisId, experienceId, componentId, null, 1, "eager"))!.Value;

        Assert.False(await store.ResetForRetryAsync(id, "on_demand"));

        await store.SetSectionsTotalAsync(id, 4);
        await store.IncrementSectionsReadyAsync(id);
        await store.SetStatusAsync(id, "failed", "boom", finished: true);
        Assert.True(await store.ResetForRetryAsync(id, "on_demand"));

        var row = await store.GetAsync(id);
        Assert.NotNull(row);
        Assert.Equal("queued", row.Status);
        Assert.Equal("on_demand", row.Trigger);
        Assert.Null(row.Error);
        Assert.Equal(0, row.SectionsTotal);
        Assert.Equal(0, row.SectionsReady);
    }

    [Fact]
    public async Task ActiveCountTracksQueuedAndRunningOnly()
    {
        var (analysisId, experienceId, componentId) = await SeedAsync("active");
        var store = Explosions;
        var id = (await store.TryCreateAsync(analysisId, experienceId, componentId, null, 1, "eager"))!.Value;

        Assert.Equal(1, await store.CountActiveForAnalysisAsync(analysisId));
        await store.SetStatusAsync(id, "running");
        Assert.Equal(1, await store.CountActiveForAnalysisAsync(analysisId));
        await store.SetStatusAsync(id, "ready", finished: true);
        Assert.Equal(0, await store.CountActiveForAnalysisAsync(analysisId));
    }

    [Fact]
    public async Task AnalysisDeleteCascadesExplosions()
    {
        var (analysisId, experienceId, componentId) = await SeedAsync("cascade");
        var store = Explosions;
        var id = (await store.TryCreateAsync(analysisId, experienceId, componentId, null, 1, "eager"))!.Value;

        await using (var del = fixture.DataSource.CreateCommand("delete from analyses where id = $1"))
        {
            del.Parameters.AddWithValue(analysisId);
            await del.ExecuteNonQueryAsync();
        }

        Assert.Null(await store.GetAsync(id));
    }

    [Fact]
    public async Task SubComponentsAreInvisibleToTopLevelReads()
    {
        var (analysisId, _, componentId) = await SeedAsync("subs");
        var analysis = new AnalysisStore(fixture.DataSource);

        var subs = await analysis.InsertSubComponentsAsync(analysisId, componentId, 1,
            [
                new Component("core/api", ["src/core/api"], ["src/core/api/x.cs"], ["src/core/api/x.cs"]),
                new Component("core/db", ["src/core/db"], ["src/core/db/y.cs"], ["src/core/db/y.cs"]),
            ]);
        await analysis.InsertSummaryAsync(analysisId, "component", subs[0].Id, "sub prose", null, "m", "v1");

        var topLevel = await analysis.GetComponentsAsync(analysisId);
        Assert.Single(topLevel);
        Assert.Equal("core", topLevel[0].Name);
        Assert.Empty(await analysis.GetComponentSummariesAsync(analysisId));

        var scoped = await analysis.GetScopedComponentSummariesAsync(analysisId, componentId);
        Assert.Single(scoped);
        Assert.Equal("core/api", scoped[0].Component);

        var children = await analysis.GetSubComponentsAsync(componentId);
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(componentId, c.ParentComponentId));

        // Re-insert replaces (idempotent on scope re-explode).
        await analysis.InsertSubComponentsAsync(analysisId, componentId, 1,
            [new Component("core/only", ["src/core/only"], ["src/core/only/z.cs"], ["src/core/only/z.cs"])]);
        Assert.Single(await analysis.GetSubComponentsAsync(componentId));
    }

    [Fact]
    public async Task TrySetPriorityOnlyWhileWaiting()
    {
        var queue = new JobQueue(fixture.DataSource);
        var jobId = await queue.EnqueueAsync("explode-scope", "{}", priority: -10);

        Assert.True(await queue.TrySetPriorityAsync(jobId, 5));
        var running = await queue.TryDequeueAsync(["explode-scope"], "w1");
        Assert.NotNull(running);
        Assert.Equal(5, running.Priority);
        Assert.False(await queue.TrySetPriorityAsync(jobId, 100));
    }

    [Fact]
    public async Task ChildSectionHelpersScopeToContentChildren()
    {
        var (analysisId, experienceId, componentId) = await SeedAsync("children");
        var experiences = new ExperienceStore(fixture.DataSource);

        var parentId = await experiences.CreateSectionAsync(
            experienceId, 100, "dd-core", "deep-dive", "Inside core", "",
            depth: 1, parentSectionId: null, componentId: componentId);
        var childA = await experiences.CreateSectionAsync(
            experienceId, 101, "dd-core-tour", "deep-dive-tour", "Tour", "",
            depth: 2, parentSectionId: parentId, componentId: componentId);
        await experiences.CreateSectionAsync(
            experienceId, 102, "dd-core-flow", "deep-dive-flow", "Flow", "",
            depth: 2, parentSectionId: parentId, componentId: componentId);
        var nestedDive = await experiences.CreateSectionAsync(
            experienceId, 103, "dd-core-api", "deep-dive", "Inside core/api", "",
            depth: 2, parentSectionId: parentId, componentId: componentId);

        Assert.Equal(2, await experiences.CountUnreadyChildSectionsAsync(parentId));
        await experiences.SetSectionStatusAsync(childA, "ready");
        Assert.Equal(1, await experiences.CountUnreadyChildSectionsAsync(parentId));

        await experiences.DeleteChildSectionsAsync(parentId);

        var sessions = new SessionStore(fixture.DataSource);
        var userId = await sessions.GetOrCreateUserAsync("explosion-children", "Explosion Tests");
        var rows = await experiences.GetSectionsAsync(experienceId, userId);
        Assert.Equal(2, rows.Count); // dd parent + nested dive survive
        Assert.Contains(rows, r => r.Id == parentId);
        Assert.Contains(rows, r => r.Id == nestedDive);
        var parentRow = rows.Single(r => r.Id == parentId);
        Assert.Equal(1, parentRow.Depth);
        Assert.Equal(componentId, parentRow.ComponentId);
        _ = analysisId;
    }
}
