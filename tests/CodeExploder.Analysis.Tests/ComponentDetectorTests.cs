using System.Globalization;
using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Analysis.Tests;

public sealed class ComponentDetectorTests
{
    private static RepoFile F(string path, int rank = 0, bool excluded = false) =>
        new(path, "C#", 100, excluded, excluded ? "vendored" : null, FileRole.None, 0, rank);

    private static RepoMap MapOf(params RepoFile[] files) =>
        new(files, [], [], [], [], [], 0, 0, files.Where(f => !f.Excluded).Sum(f => f.SizeBytes));

    [Fact]
    public void ManifestDirectoriesBecomeComponents()
    {
        var map = MapOf(
            F("src/A/A.csproj", rank: 500),
            F("src/A/One.cs", rank: 10),
            F("src/A/Two.cs"),
            F("src/A/Three.cs"),
            F("src/B/B.csproj", rank: 500),
            F("src/B/One.cs"),
            F("src/B/Two.cs"),
            F("src/B/Three.cs"),
            F("README.md"),
            F("Notes.txt"),
            F("Main.cs"),
            F("skipped.dll", excluded: true));

        var components = new ComponentDetector().Detect(map);

        Assert.Equal(3, components.Count);
        var a = Assert.Single(components, c => c.Name == "A");
        Assert.Equal(4, a.FilePaths.Count);
        Assert.Equal("src/A/A.csproj", a.TopFiles[0]);
        Assert.Single(components, c => c.Name == "B");
        var root = Assert.Single(components, c => c.Name == "(root)");
        Assert.Equal(3, root.FilePaths.Count);
        Assert.DoesNotContain("skipped.dll", components.SelectMany(c => c.FilePaths));
    }

    [Fact]
    public void NoManifestsFallsBackToTopLevelDirectories()
    {
        var map = MapOf(
            F("x/a.cs"), F("x/b.cs"), F("x/c.cs"),
            F("y/a.cs"), F("y/b.cs"), F("y/c.cs"),
            F("loose1.cs"), F("loose2.cs"), F("loose3.cs"));

        var components = new ComponentDetector().Detect(map);

        Assert.Equal(3, components.Count);
        Assert.Contains(components, c => c.Name == "x");
        Assert.Contains(components, c => c.Name == "y");
        Assert.Contains(components, c => c.Name == "(root)");
    }

    [Fact]
    public void TinyComponentsMergeIntoRoot()
    {
        var map = MapOf(
            F("src/A/A.csproj"),
            F("src/A/One.cs"),
            F("README.md"),
            F("Main.cs"),
            F("Notes.txt"));

        var components = new ComponentDetector().Detect(map);

        var root = Assert.Single(components);
        Assert.Equal("(root)", root.Name);
        Assert.Equal(5, root.FilePaths.Count);
    }

    [Fact]
    public void OversizedComponentSplitsByImmediateSubdirectory()
    {
        var files = new List<RepoFile>
        {
            F("src/big/package.json", rank: 500),
            F("src/big/app.js"),
            F("src/big/setup.js"),
            F("src/big/extra.js"),
            F("src/big/more.js"),
        };
        for (var i = 0; i < 90; i++)
        {
            files.Add(F("src/big/sub1/f" + i.ToString(CultureInfo.InvariantCulture) + ".js"));
        }

        for (var i = 0; i < 80; i++)
        {
            files.Add(F("src/big/sub2/f" + i.ToString(CultureInfo.InvariantCulture) + ".js"));
        }

        var components = new ComponentDetector().Detect(MapOf([.. files]));

        Assert.Equal(3, components.Count);
        Assert.Equal(90, Assert.Single(components, c => c.Name == "src/big/sub1").FilePaths.Count);
        Assert.Equal(80, Assert.Single(components, c => c.Name == "src/big/sub2").FilePaths.Count);
        Assert.Equal(5, Assert.Single(components, c => c.Name == "src/big").FilePaths.Count);
    }

    [Fact]
    public void ComponentCountIsCappedByMergingSmallestIntoRoot()
    {
        var files = new List<RepoFile>();
        for (var d = 0; d < 45; d++)
        {
            var dir = "d" + d.ToString("D2", CultureInfo.InvariantCulture);
            files.Add(F(dir + "/a.cs"));
            files.Add(F(dir + "/b.cs"));
            files.Add(F(dir + "/c.cs"));
        }

        var components = new ComponentDetector().Detect(MapOf([.. files]));

        Assert.Equal(40, components.Count);
        Assert.Equal(135, components.Sum(c => c.FilePaths.Count));
        var root = Assert.Single(components, c => c.Name == "(root)");
        Assert.Equal(18, root.FilePaths.Count);
    }

    [Fact]
    public void EveryNonExcludedFileIsAssignedExactlyOnce()
    {
        var map = MapOf(
            F("src/A/A.csproj"),
            F("src/A/One.cs"),
            F("src/A/Two.cs"),
            F("src/A/inner/Three.cs"),
            F("tools/gen.py"),
            F("README.md"));

        var components = new ComponentDetector().Detect(map);

        var assigned = components.SelectMany(c => c.FilePaths).ToList();
        Assert.Equal(6, assigned.Count);
        Assert.Equal(6, assigned.Distinct(StringComparer.Ordinal).Count());
    }
}
