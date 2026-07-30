using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Analysis.Tests;

public sealed class RunPlannerTests
{
    [Fact]
    public void BuildSummaryAssemblesCountsFaithfully()
    {
        var files = new[]
        {
            new RepoFile("a.cs", "C#", 120, false, null, FileRole.EntryPoint, 3, 1010),
            new RepoFile("b.cs", "C#", 80, false, null, FileRole.None, 0, 6),
            new RepoFile("vendor/", "", 0, true, "vendored", FileRole.None, 0, 0),
        };
        var map = new RepoMap(
            files,
            [new LanguageStat("C#", 2, 200, 100)],
            ["msbuild"],
            [".github/workflows/ci.yml"],
            ["a.cs"],
            [new ChurnStat("a.cs", 3)],
            CommitCount: 12,
            ContributorCount: 4,
            TotalBytes: 200);
        var components = new[]
        {
            new Component("core", ["src"], ["a.cs", "b.cs"], ["a.cs"]),
        };

        var summary = RunPlanner.BuildSummary("abc123", "a test repo", map, components, chunkCount: 7);

        Assert.Equal("abc123", summary.CommitSha);
        Assert.Equal("a test repo", summary.Description);
        Assert.Equal(3, summary.FileCount);
        Assert.Equal(2, summary.AnalyzedFileCount);
        Assert.Equal(1, summary.ExcludedFileCount);
        Assert.Equal(7, summary.ChunkCount);
        Assert.Equal(200, summary.TotalBytes);
        Assert.Equal(map.Languages, summary.Languages);
        Assert.Equal(map.BuildSystems, summary.BuildSystems);
        Assert.Equal(map.CiConfigs, summary.CiConfigs);
        Assert.Equal(map.EntryPoints, summary.EntryPoints);
        var component = Assert.Single(summary.Components);
        Assert.Equal("core", component.Name);
        Assert.Equal(2, component.FileCount);
        Assert.Equal("a.cs", Assert.Single(component.TopFiles));
        Assert.Equal(map.TopChurnFiles, summary.TopChurnFiles);
        Assert.Equal(12, summary.CommitCount);
        Assert.Equal(4, summary.ContributorCount);
    }
}
