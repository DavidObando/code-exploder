using System.Globalization;
using CodeExploder.Domain;

namespace CodeExploder.Analysis;

/// <summary>
/// Deterministic history mining for the origin story (docs/08 §M9): parses the full
/// git log and segments the repository's life into eras — by activity gaps when the
/// history spans real time, by commit-count chunks when it doesn't — annotating each
/// era with its cast, component births, and notable moments. No LLM involved.
/// </summary>
public static class HistoryMiner
{
    public const int MaxCommits = 5_000;
    public const int MaxEras = 5;
    private static readonly TimeSpan EraGap = TimeSpan.FromDays(45);

    /// <summary>Parses FullLogAsync output: "@@sha|author|iso-date|subject" + path lines.</summary>
    public static IReadOnlyList<HistoryCommit> ParseLog(string log)
    {
        var commits = new List<HistoryCommit>();
        string? sha = null, author = null, subject = null;
        DateTimeOffset at = default;
        List<string> paths = [];

        void Flush()
        {
            if (sha is not null)
            {
                commits.Add(new HistoryCommit(sha, author!, at, subject!, paths));
            }

            paths = [];
        }

        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                Flush();
                var parts = line[2..].Split('|', 4);
                if (parts.Length == 4
                    && DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
                {
                    sha = parts[0];
                    author = parts[1];
                    subject = parts[3];
                }
                else
                {
                    sha = null;
                }
            }
            else if (line.Length > 0 && sha is not null)
            {
                paths.Add(line);
            }
        }

        Flush();
        return commits;
    }

    public static HistoryDoc Mine(IReadOnlyList<HistoryCommit> commits, IReadOnlyList<Component> components)
    {
        if (commits.Count == 0)
        {
            throw new InvalidOperationException("history is empty");
        }

        var contributors = commits.GroupBy(c => c.Author, StringComparer.Ordinal)
            .Select(g => new Contributor(g.Key, g.Count()))
            .OrderByDescending(c => c.Commits)
            .Take(12)
            .ToList();

        var eraRanges = SegmentEras(commits);
        var componentBirths = FindComponentBirths(commits, components);
        var firstTest = commits.FirstOrDefault(c => c.Paths.Any(IsTestPath));
        var firstCi = commits.FirstOrDefault(c => c.Paths.Any(IsCiPath));

        var eras = new List<HistoryEra>();
        for (var i = 0; i < eraRanges.Count; i++)
        {
            var era = eraRanges[i];
            var moments = new List<HistoryMoment>();
            if (i == 0)
            {
                moments.Add(Moment("first-commit", commits[0], "where it all began"));
            }

            if (firstTest is not null && InRange(firstTest, era))
            {
                moments.Add(Moment("first-test", firstTest, "the first test arrives"));
            }

            if (firstCi is not null && InRange(firstCi, era))
            {
                moments.Add(Moment("first-ci", firstCi, "continuous integration begins"));
            }

            foreach (var (component, birth) in componentBirths.Where(b => InRange(b.Value, era)))
            {
                moments.Add(Moment("component-born", birth, $"{component} is born"));
            }

            var biggest = era.Commits.OrderByDescending(c => c.Paths.Count).First();
            if (biggest.Paths.Count >= 3 && !moments.Any(m => m.ShaShort == Short(biggest.Sha)))
            {
                moments.Add(Moment("biggest-change", biggest, "the era's biggest single change"));
            }

            eras.Add(new HistoryEra(
                i,
                era.Commits[0].At,
                era.Commits[^1].At,
                era.Commits.Count,
                era.Commits.GroupBy(c => c.Author, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key).ToList(),
                era.Commits.SelectMany(c => c.Paths).Distinct(StringComparer.Ordinal).Count(),
                componentBirths.Where(b => InRange(b.Value, era)).Select(b => b.Key).ToList(),
                moments.OrderBy(m => m.At).Take(6).ToList()));
        }

        return new HistoryDoc(
            commits.Count,
            commits.Count >= MaxCommits,
            commits[0].At,
            commits[^1].At,
            contributors,
            eras);
    }

    private sealed record EraRange(List<HistoryCommit> Commits);

    private static List<EraRange> SegmentEras(IReadOnlyList<HistoryCommit> commits)
    {
        // Gap-based first: a quiet stretch longer than EraGap starts a new era.
        var ranges = new List<EraRange> { new([commits[0]]) };
        for (var i = 1; i < commits.Count; i++)
        {
            if (commits[i].At - commits[i - 1].At > EraGap)
            {
                ranges.Add(new EraRange([]));
            }

            ranges[^1].Commits.Add(commits[i]);
        }

        // Young/dense histories don't gap — fall back to count chunks so the story
        // still has chapters. Then clamp to MaxEras by merging the smallest spans.
        if (ranges.Count == 1 && commits.Count >= 12)
        {
            var chunk = (int)Math.Ceiling(commits.Count / (double)Math.Min(MaxEras, commits.Count / 4));
            ranges = commits.Chunk(chunk).Select(c => new EraRange([.. c])).ToList();
        }

        while (ranges.Count > MaxEras)
        {
            var smallest = ranges.OrderBy(r => r.Commits.Count).First();
            var index = ranges.IndexOf(smallest);
            var neighbor = index == 0 ? 1 : index - 1;
            ranges[neighbor].Commits.AddRange(smallest.Commits);
            ranges[neighbor].Commits.Sort((a, b) => a.At.CompareTo(b.At));
            ranges.RemoveAt(index);
        }

        return ranges;
    }

    private static Dictionary<string, HistoryCommit> FindComponentBirths(
        IReadOnlyList<HistoryCommit> commits, IReadOnlyList<Component> components)
    {
        var births = new Dictionary<string, HistoryCommit>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            var roots = component.RootPaths
                .Select(r => r.TrimEnd('/'))
                .Where(r => r.Length > 0)
                .ToList();
            if (roots.Count == 0)
            {
                continue;
            }

            var birth = commits.FirstOrDefault(c => c.Paths.Any(p =>
                roots.Any(r => p.StartsWith(r + "/", StringComparison.Ordinal) || p == r)));
            if (birth is not null)
            {
                births[component.Name] = birth;
            }
        }

        return births;
    }

    private static bool InRange(HistoryCommit commit, EraRange era) =>
        commit.At >= era.Commits[0].At && commit.At <= era.Commits[^1].At;

    private static bool IsTestPath(string path) =>
        path.Split('/').Any(s => s is "test" or "tests" or "__tests__" or "spec")
        || path.Contains(".test.", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Tests", StringComparison.Ordinal);

    private static bool IsCiPath(string path) =>
        path.StartsWith(".github/workflows/", StringComparison.Ordinal)
        || path is "Jenkinsfile" or ".gitlab-ci.yml" or "azure-pipelines.yml"
        || path.StartsWith(".circleci/", StringComparison.Ordinal);

    private static HistoryMoment Moment(string kind, HistoryCommit commit, string detail) => new(
        kind, Short(commit.Sha), commit.At, commit.Author, commit.Subject, commit.Paths.Count, detail);

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
