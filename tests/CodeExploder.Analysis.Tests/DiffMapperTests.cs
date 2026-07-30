using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Analysis.Tests;

public sealed class DiffMapperTests
{
    private const string SampleDiff = """
        diff --git a/src/App/Program.cs b/src/App/Program.cs
        index 111..222 100644
        --- a/src/App/Program.cs
        +++ b/src/App/Program.cs
        @@ -10,4 +10,6 @@ void Main()
         line kept
        -old line
        +new line
        +another new line
         context
        diff --git a/src/App/New.cs b/src/App/New.cs
        new file mode 100644
        index 000..333
        --- /dev/null
        +++ b/src/App/New.cs
        @@ -0,0 +1,2 @@
        +created a
        +created b
        diff --git a/tests/AppTests/OldTests.cs b/tests/AppTests/OldTests.cs
        deleted file mode 100644
        index 444..000
        --- a/tests/AppTests/OldTests.cs
        +++ /dev/null
        @@ -1,2 +0,0 @@
        -gone a
        -gone b
        """;

    private static RepoMap EmptyMap => new([], [], [], [], [], [], 0, 0, 0);

    private static readonly Component AppComponent =
        new("App", ["src/App"], ["src/App/Program.cs", "src/App/New.cs"], []);

    private static readonly Component TestComponent =
        new("AppTests", ["tests/AppTests"], ["tests/AppTests/OldTests.cs"], []);

    [Fact]
    public void ParsesChangeKindsHunksAndCounts()
    {
        var files = DiffMapper.Map(SampleDiff, EmptyMap, [AppComponent, TestComponent]);

        Assert.Equal(3, files.Count);

        var modified = files.Single(f => f.Path == "src/App/Program.cs");
        Assert.Equal("modified", modified.ChangeKind);
        Assert.Equal(2, modified.Additions);
        Assert.Equal(1, modified.Deletions);
        Assert.Single(modified.Hunks);
        Assert.Equal(10, modified.Hunks[0].NewStart);
        Assert.Contains("+new line", modified.Hunks[0].Text, StringComparison.Ordinal);
        Assert.Equal("App", modified.Component);
        Assert.False(modified.IsTest);

        var added = files.Single(f => f.Path == "src/App/New.cs");
        Assert.Equal("added", added.ChangeKind);
        Assert.Equal(2, added.Additions);

        var deleted = files.Single(f => f.Path == "tests/AppTests/OldTests.cs");
        Assert.Equal("deleted", deleted.ChangeKind);
        Assert.True(deleted.IsTest);
        Assert.Equal("AppTests", deleted.Component);
    }

    [Fact]
    public void PrDiffAggregatesTouchedComponentsAndTotals()
    {
        var files = DiffMapper.Map(SampleDiff, EmptyMap, [AppComponent, TestComponent]);
        var pr = new PrDiff(42, "t", null, "main", files);

        Assert.Equal(["App", "AppTests"], pr.TouchedComponents.Order().ToList());
        Assert.Equal(4, pr.TotalAdditions);
        Assert.Equal(3, pr.TotalDeletions);
    }

    [Fact]
    public void RenamesCarryOldPath()
    {
        const string renameDiff = """
            diff --git a/src/App/A.cs b/src/App/B.cs
            similarity index 90%
            rename from src/App/A.cs
            rename to src/App/B.cs
            index 111..222 100644
            --- a/src/App/A.cs
            +++ b/src/App/B.cs
            @@ -1,2 +1,2 @@
            -x
            +y
             z
            """;
        var files = DiffMapper.Map(renameDiff, EmptyMap, [AppComponent]);

        var renamed = Assert.Single(files);
        Assert.Equal("renamed", renamed.ChangeKind);
        Assert.Equal("src/App/B.cs", renamed.Path);
        Assert.Equal("src/App/A.cs", renamed.OldPath);
    }
}
