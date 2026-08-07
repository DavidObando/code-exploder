using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Analysis.Tests;

public sealed class DetectWithinTests
{
    private static RepoFile F(string path, int rank = 0, int churn = 0, bool excluded = false) =>
        new(path, "C#", 100, excluded, excluded ? "vendored" : null, FileRole.None, churn, rank);

    private static RepoMap MapOf(params RepoFile[] files) =>
        new(files, [], [], [], [], [], 0, 0, files.Where(f => !f.Excluded).Sum(f => f.SizeBytes));

    private static Component Comp(string name, string root, params string[] paths) =>
        new(name, [root], paths, paths.Take(5).ToList());

    [Fact]
    public void FindsSubdirectoriesAsSubComponents()
    {
        var paths = new List<string>();
        foreach (var dir in new[] { "core", "render", "io" })
        {
            for (var i = 0; i < 4; i++)
            {
                paths.Add($"src/Engine/{dir}/file{i}.cs");
            }
        }

        var map = MapOf(paths.Select(p => F(p)).ToArray());
        var parent = Comp("Engine", "src/Engine", paths.ToArray());

        var subs = ComponentDetector.DetectWithin(map, parent);

        Assert.Equal(3, subs.Count);
        Assert.All(subs, s => Assert.StartsWith("Engine/", s.Name, StringComparison.Ordinal));
        var core = Assert.Single(subs, s => s.Name == "Engine/core");
        Assert.All(core.FilePaths, p => Assert.StartsWith("src/Engine/core/", p, StringComparison.Ordinal));
        Assert.Contains("src/Engine/core", core.RootPaths);
    }

    [Fact]
    public void AtomicScopeReturnsEmpty()
    {
        var paths = Enumerable.Range(0, 6).Select(i => $"src/Tiny/file{i}.cs").ToArray();
        var map = MapOf(paths.Select(p => F(p)).ToArray());
        var parent = Comp("Tiny", "src/Tiny", paths);

        Assert.Empty(ComponentDetector.DetectWithin(map, parent));
    }

    [Fact]
    public void LooseFilesFormRootSubComponent()
    {
        var paths = new List<string> { "src/App/Program.cs", "src/App/Startup.cs" };
        for (var i = 0; i < 5; i++)
        {
            paths.Add($"src/App/handlers/h{i}.cs");
            paths.Add($"src/App/models/m{i}.cs");
        }

        var map = MapOf(paths.Select(p => F(p)).ToArray());
        var parent = Comp("App", "src/App", paths.ToArray());

        var subs = ComponentDetector.DetectWithin(map, parent);

        Assert.Equal(3, subs.Count);
        Assert.Contains(subs, s => s.Name == "App/handlers");
        Assert.Contains(subs, s => s.Name == "App/models");
        var root = Assert.Single(subs, s => s.Name == "App/(root)");
        Assert.Equal(2, root.FilePaths.Count);
    }

    [Fact]
    public void ExcludedAndForeignFilesAreIgnored()
    {
        var paths = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            paths.Add($"lib/X/alpha/a{i}.cs");
            paths.Add($"lib/X/beta/b{i}.cs");
        }

        var files = paths.Select(p => F(p)).ToList();
        files.Add(F("lib/X/alpha/generated.cs", excluded: true));
        files.Add(F("elsewhere/other.cs"));
        var parent = Comp("X", "lib/X", [.. paths, "lib/X/alpha/generated.cs"]);

        var subs = ComponentDetector.DetectWithin(MapOf([.. files]), parent);

        Assert.Equal(2, subs.Count);
        Assert.DoesNotContain(subs.SelectMany(s => s.FilePaths),
            p => p.Contains("generated", StringComparison.Ordinal) || p.StartsWith("elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public void DeterministicAcrossRuns()
    {
        var paths = new List<string>();
        foreach (var dir in new[] { "a", "b", "c" })
        {
            for (var i = 0; i < 3; i++)
            {
                paths.Add($"pkg/{dir}/f{i}.cs");
            }
        }

        var map = MapOf(paths.Select(p => F(p)).ToArray());
        var parent = Comp("pkg", "pkg", paths.ToArray());

        var first = ComponentDetector.DetectWithin(map, parent);
        var second = ComponentDetector.DetectWithin(map, parent);

        Assert.Equal(
            first.Select(s => (s.Name, string.Join('|', s.FilePaths))),
            second.Select(s => (s.Name, string.Join('|', s.FilePaths))));
    }

    [Theory]
    [InlineData(new[] { "src/A/x.cs", "src/A/y/z.cs" }, "src/A/")]
    [InlineData(new[] { "src/A/x.cs", "src/B/y.cs" }, "src/")]
    [InlineData(new[] { "root.cs", "src/A/x.cs" }, "")]
    [InlineData(new[] { "src/A/x.cs" }, "src/A/")]
    public void CommonDirectoryPrefixIsCorrect(string[] paths, string expected)
    {
        Assert.Equal(expected, ComponentDetector.CommonDirectoryPrefix(paths));
    }
}
