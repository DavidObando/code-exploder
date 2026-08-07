using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Storage.Tests;

/// <summary>
/// The launch contract (M10): first launch creates row + deep-dive section + queued
/// explode-scope job and announces DeepDivePlanned; duplicates are idempotent; an
/// on-demand request upgrades a queued eager dive's priority; failed dives relaunch.
/// </summary>
public sealed class ExplosionLauncherTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private sealed class RecordingBus : ISessionEventBus
    {
        public List<(Guid SessionId, string Kind, object Data)> Events { get; } = [];

        public void Publish(Guid sessionId, string kind, object data) => Events.Add((sessionId, kind, data));

        public void PublishTransient(Guid sessionId, string kind, object data) => Events.Add((sessionId, kind, data));
    }

    private (ExplosionLauncher Launcher, ExplosionStore Explosions, ExperienceStore Experiences, JobQueue Queue, RecordingBus Bus) Build()
    {
        var explosions = new ExplosionStore(fixture.DataSource);
        var experiences = new ExperienceStore(fixture.DataSource);
        var queue = new JobQueue(fixture.DataSource);
        var bus = new RecordingBus();
        var launcher = new ExplosionLauncher(explosions, experiences, queue, bus, new ExplosionOptions());
        return (launcher, explosions, experiences, queue, bus);
    }

    private async Task<ExplosionRequest> SeedRequestAsync(string marker, string trigger)
    {
        var sessions = new SessionStore(fixture.DataSource);
        var userId = await sessions.GetOrCreateUserAsync($"launcher-{marker}", "Launcher Tests");
        var repoId = await sessions.GetOrCreateRepoAsync("octo", $"launch-{marker}", $"https://github.com/octo/launch-{marker}");
        var analysisId = await sessions.CreateAnalysisAsync(repoId, "repo", null);
        var sessionId = await sessions.CreateSessionAsync(userId, analysisId, "repo", $"octo/launch-{marker}");

        var analysis = new AnalysisStore(fixture.DataSource);
        await analysis.InsertComponentsAsync(analysisId,
            [new Component("Core Engine", ["src/core"], ["src/core/a.cs"], ["src/core/a.cs"])]);
        var componentId = (await analysis.GetComponentsAsync(analysisId))[0].Id;

        var experiences = new ExperienceStore(fixture.DataSource);
        var experienceId = await experiences.CreateExperienceAsync(sessionId, "abc123", "test-model");
        var anchorId = await experiences.CreateSectionAsync(experienceId, 1, "architecture", "architecture", "Architecture", "");

        return new ExplosionRequest(
            analysisId, sessionId, experienceId, componentId, "Core Engine",
            ExplosionDepth: 1, ParentExplosionId: null, AnchorSectionId: anchorId, SectionDepth: 1,
            trigger, "octo", $"launch-{marker}", null);
    }

    [Fact]
    public async Task FirstLaunchCreatesEverything()
    {
        var (launcher, _, experiences, queue, bus) = Build();
        var request = await SeedRequestAsync("first", ExplosionTrigger.OnDemand);

        var launch = await launcher.LaunchAsync(request);

        Assert.True(launch.Created);
        Assert.Equal(ExplosionStatus.Queued, launch.Explosion.Status);
        Assert.NotNull(launch.Explosion.SectionId);
        Assert.NotNull(launch.Explosion.QueueJobId);

        var sessions = new SessionStore(fixture.DataSource);
        var userId = await sessions.GetOrCreateUserAsync("launcher-first", "Launcher Tests");
        var rows = await experiences.GetSectionsAsync(request.ExperienceId, userId);
        var dd = Assert.Single(rows, r => r.Kind == "deep-dive");
        Assert.Equal("dd-core-engine", dd.Slug);
        Assert.Equal("Deep dive: Core Engine", dd.Title);
        Assert.Equal(1, dd.Depth);
        Assert.Equal(request.AnchorSectionId, dd.ParentSectionId);
        Assert.Equal(request.ComponentId, dd.ComponentId);

        var job = await queue.TryDequeueAsync(["explode-scope"], "w-test");
        Assert.NotNull(job);
        Assert.Equal(5, job.Priority);
        using var payload = System.Text.Json.JsonDocument.Parse(job.PayloadJson);
        Assert.Equal("Core Engine", payload.RootElement.GetProperty("componentName").GetString());
        Assert.Equal(1, payload.RootElement.GetProperty("explosionDepth").GetInt32());

        Assert.Contains(bus.Events, e => e.Kind == "DeepDivePlanned" && e.SessionId == request.SessionId);
    }

    [Fact]
    public async Task DuplicateLaunchIsIdempotent()
    {
        var (launcher, _, _, _, _) = Build();
        var request = await SeedRequestAsync("dup", ExplosionTrigger.OnDemand);

        var first = await launcher.LaunchAsync(request);
        var second = await launcher.LaunchAsync(request);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Explosion.Id, second.Explosion.Id);
    }

    [Fact]
    public async Task OnDemandUpgradesQueuedEagerDive()
    {
        var (launcher, _, _, queue, _) = Build();
        var request = await SeedRequestAsync("upgrade", ExplosionTrigger.Eager);

        var eager = await launcher.LaunchAsync(request);
        Assert.Equal(ExplosionTrigger.Eager, eager.Explosion.Trigger);

        var upgraded = await launcher.LaunchAsync(request with { Trigger = ExplosionTrigger.OnDemand });

        Assert.False(upgraded.Created);
        Assert.Equal(ExplosionTrigger.OnDemand, upgraded.Explosion.Trigger);
        var job = await queue.TryDequeueAsync(["explode-scope"], "w-upgrade");
        Assert.NotNull(job);
        Assert.Equal(5, job.Priority);
    }

    [Fact]
    public async Task FailedDiveRelaunches()
    {
        var (launcher, explosions, experiences, queue, bus) = Build();
        var request = await SeedRequestAsync("relaunch", ExplosionTrigger.OnDemand);
        var launch = await launcher.LaunchAsync(request);

        // Simulate the dive dying, then the retry endpoint's reset+relaunch.
        var drained = await queue.TryDequeueAsync(["explode-scope"], "w-re");
        Assert.NotNull(drained);
        await queue.CompleteAsync(drained.Id);
        await explosions.SetStatusAsync(launch.Explosion.Id, ExplosionStatus.Failed, "boom", finished: true);
        await experiences.SetSectionStatusAsync(launch.Explosion.SectionId!.Value, SectionState.Failed);

        Assert.True(await explosions.ResetForRetryAsync(launch.Explosion.Id, ExplosionTrigger.OnDemand));
        var row = await explosions.GetAsync(launch.Explosion.Id);
        await launcher.RelaunchAsync(request, row!);

        var job = await queue.TryDequeueAsync(["explode-scope"], "w-re2");
        Assert.NotNull(job);
        var sessions = new SessionStore(fixture.DataSource);
        var userId = await sessions.GetOrCreateUserAsync("launcher-relaunch", "Launcher Tests");
        var dd = Assert.Single(await experiences.GetSectionsAsync(request.ExperienceId, userId), r => r.Kind == "deep-dive");
        Assert.Equal(SectionState.Pending, dd.Status);
        Assert.Equal(2, bus.Events.Count(e => e.Kind == "DeepDivePlanned"));
    }

    [Fact]
    public async Task LaunchOnFailedDiveRelaunches()
    {
        var (launcher, explosions, _, queue, _) = Build();
        var request = await SeedRequestAsync("failedlaunch", ExplosionTrigger.OnDemand);
        var launch = await launcher.LaunchAsync(request);
        var drained = await queue.TryDequeueAsync(["explode-scope"], "w-fl");
        Assert.NotNull(drained);
        await queue.CompleteAsync(drained.Id);
        await explosions.SetStatusAsync(launch.Explosion.Id, ExplosionStatus.Failed, "boom", finished: true);

        var second = await launcher.LaunchAsync(request);

        Assert.False(second.Created);
        Assert.Equal(ExplosionStatus.Queued, second.Explosion.Status);
        Assert.NotNull(await queue.TryDequeueAsync(["explode-scope"], "w-fl2"));
    }

    [Theory]
    [InlineData("Core Engine", "core-engine")]
    [InlineData("src/CodeExploder.Pipeline", "src-codeexploder-pipeline")]
    [InlineData("(root)", "root")]
    [InlineData("///", "scope")]
    public void SlugifyIsSane(string name, string expected)
    {
        Assert.Equal(expected, ExplosionLauncher.Slugify(name));
    }
}
