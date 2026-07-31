using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Analysis.Tests;

public sealed class HistoryMinerTests
{
    [Fact]
    public void ParsesLogEntriesWithPaths()
    {
        const string log = """
            @@aaa1111|Alice|2021-03-01T10:00:00+00:00|Initial commit
            src/App/Program.cs
            README.md

            @@bbb2222|Bob|2021-03-02T10:00:00+00:00|Add tests
            tests/AppTests/FirstTests.cs
            """;
        var commits = HistoryMiner.ParseLog(log);

        Assert.Equal(2, commits.Count);
        Assert.Equal("aaa1111", commits[0].Sha);
        Assert.Equal("Alice", commits[0].Author);
        Assert.Equal(["src/App/Program.cs", "README.md"], commits[0].Paths);
        Assert.Equal("Add tests", commits[1].Subject);
    }

    [Fact]
    public void GapSegmentationSplitsErasAndFindsMoments()
    {
        var commits = new List<HistoryCommit>();
        var start = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 10; i++)
        {
            commits.Add(new HistoryCommit($"a{i:D7}", "Alice", start.AddDays(i), $"early {i}", ["src/App/F.cs"]));
        }

        // 90-day quiet gap → second era, where tests and CI arrive.
        var later = start.AddDays(120);
        commits.Add(new HistoryCommit("b0000001", "Bob", later, "revival", ["src/App/G.cs"]));
        commits.Add(new HistoryCommit("b0000002", "Bob", later.AddDays(1), "add tests",
            ["tests/AppTests/T.cs", ".github/workflows/ci.yml", "src/App/H.cs"]));

        var components = new[] { new Component("App", ["src/App"], ["src/App/F.cs"], []) };
        var doc = HistoryMiner.Mine(commits, components);

        Assert.Equal(2, doc.Eras.Count);
        Assert.Equal(10, doc.Eras[0].CommitCount);
        Assert.Contains(doc.Eras[0].Moments, m => m.Kind == "first-commit");
        Assert.Contains(doc.Eras[0].Moments, m => m.Kind == "component-born" && m.Detail!.Contains("App", StringComparison.Ordinal));
        Assert.Contains(doc.Eras[1].Moments, m => m.Kind == "first-test");
        Assert.Contains(doc.Eras[1].Moments, m => m.Kind == "first-ci");
        Assert.Equal("Alice", doc.Contributors[0].Name);
    }

    [Fact]
    public void DenseYoungHistoryFallsBackToCountChunks()
    {
        var start = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var commits = Enumerable.Range(0, 40)
            .Select(i => new HistoryCommit($"c{i:D7}", "Solo", start.AddMinutes(i * 30), $"c{i}", ["src/x.cs"]))
            .ToList();

        var doc = HistoryMiner.Mine(commits, []);

        Assert.InRange(doc.Eras.Count, 2, HistoryMiner.MaxEras);
        Assert.Equal(40, doc.Eras.Sum(e => e.CommitCount));
    }

    [Fact]
    public void ErasAreCappedAtMax()
    {
        var commits = new List<HistoryCommit>();
        var start = new DateTimeOffset(2015, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var era = 0; era < 9; era++)
        {
            for (var i = 0; i < 3; i++)
            {
                commits.Add(new HistoryCommit($"e{era}c{i:D5}", "A", start.AddDays(era * 100 + i), "w", ["f.cs"]));
            }
        }

        var doc = HistoryMiner.Mine(commits, []);

        Assert.True(doc.Eras.Count <= HistoryMiner.MaxEras);
        Assert.Equal(27, doc.Eras.Sum(e => e.CommitCount));
    }
}
