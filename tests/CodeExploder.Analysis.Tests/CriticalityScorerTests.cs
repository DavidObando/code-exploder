using System.Text.Json;
using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Analysis.Tests;

public sealed class CriticalityScorerTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static RepoFile F(string path, int churn = 0) =>
        new(path, "C#", 100, false, null, FileRole.None, churn, 0);

    private static Component Comp(string name, int files, int churnPerFile = 0)
    {
        var paths = Enumerable.Range(0, files).Select(i => $"{name}/f{i}.cs").ToList();
        return new Component(name, [name], paths, paths.Take(5).ToList());
    }

    private static string Doc(int risks = 0, params string[] talksTo)
    {
        var doc = new ComponentSummaryDoc(
            "p", [], [], [],
            talksTo.Select(t => new TalksTo(t, "calls", "out")).ToList(),
            [],
            [],
            Enumerable.Range(0, risks).Select(i => $"risk{i}").ToList());
        return JsonSerializer.Serialize(doc, JsonOpts);
    }

    private static RepoMap MapFor(params Component[] comps)
    {
        var files = comps.SelectMany(c => c.FilePaths.Select(p => F(p))).ToList();
        return new RepoMap(files, [], [], [], [], [], 0, 0, 0);
    }

    [Fact]
    public void FanInDominatesRanking()
    {
        var hub = Comp("hub", 10);
        var a = Comp("a", 10);
        var b = Comp("b", 10);
        var summaries = new Dictionary<string, string?>
        {
            ["hub"] = Doc(),
            ["a"] = Doc(0, "hub"),
            ["b"] = Doc(0, "hub"),
        };

        var ranked = CriticalityScorer.Rank([hub, a, b], MapFor(hub, a, b), summaries, minScopeFiles: 8);

        Assert.Equal("hub", ranked[0].ComponentName);
        Assert.True(ranked[0].Score >= 6.0);
    }

    [Fact]
    public void RisksRaiseScore()
    {
        var risky = Comp("risky", 10);
        var calm = Comp("calm", 10);
        var summaries = new Dictionary<string, string?> { ["risky"] = Doc(risks: 3), ["calm"] = Doc() };

        var ranked = CriticalityScorer.Rank([risky, calm], MapFor(risky, calm), summaries, minScopeFiles: 8);

        Assert.Equal("risky", ranked[0].ComponentName);
    }

    [Fact]
    public void SmallScopesAreExcluded()
    {
        var big = Comp("big", 10);
        var small = Comp("small", 3);

        var ranked = CriticalityScorer.Rank(
            [big, small], MapFor(big, small), new Dictionary<string, string?>(), minScopeFiles: 8);

        Assert.Single(ranked);
        Assert.Equal("big", ranked[0].ComponentName);
    }

    [Fact]
    public void MalformedOrMissingSummariesScoreZeroTerms()
    {
        var a = Comp("a", 10);
        var summaries = new Dictionary<string, string?> { ["a"] = "not json {", ["ghost"] = null };

        var ranked = CriticalityScorer.Rank([a], MapFor(a), summaries, minScopeFiles: 8);

        var only = Assert.Single(ranked);
        // Only the size term remains: 10/25.
        Assert.Equal(0.4, only.Score, precision: 5);
    }

    [Fact]
    public void SizeAndChurnTermsAreCapped()
    {
        var huge = Comp("huge", 500);
        var files = huge.FilePaths.Select(p => F(p, churn: 100)).ToList();
        var map = new RepoMap(files, [], [], [], [], [], 0, 0, 0);

        var ranked = CriticalityScorer.Rank([huge], map, new Dictionary<string, string?>(), minScopeFiles: 8);

        // size cap 4 + churn cap 4 (all churn is this component's).
        Assert.Equal(8.0, Assert.Single(ranked).Score, precision: 5);
    }

    [Fact]
    public void SelfReferenceDoesNotCountAsFanIn()
    {
        var solo = Comp("solo", 10);
        var summaries = new Dictionary<string, string?> { ["solo"] = Doc(0, "solo") };

        var ranked = CriticalityScorer.Rank([solo], MapFor(solo), summaries, minScopeFiles: 8);

        Assert.Equal(0.4, Assert.Single(ranked).Score, precision: 5);
    }
}
