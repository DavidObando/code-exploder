using System.Text.RegularExpressions;
using CodeExploder.Domain;

namespace CodeExploder.Analysis;

/// <summary>
/// Parses `git diff -M base..head` output into the deterministic PR diff map
/// (docs/01 §PR-diff mode): change classification, test-vs-prod, component mapping.
/// Hunks are capped so walkthrough sections stay bounded; the cap is recorded honestly.
/// </summary>
public static partial class DiffMapper
{
    public const int MaxHunksPerFile = 3;
    public const int MaxHunkLines = 120;

    public static IReadOnlyList<PrDiffFile> Map(
        string unifiedDiff,
        RepoMap map,
        IReadOnlyList<Component> components)
    {
        var roles = map.Files.ToDictionary(f => f.Path, f => f.Role, StringComparer.Ordinal);
        var componentByFile = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var path in component.FilePaths)
            {
                componentByFile[path] = component.Name;
            }
        }

        var files = new List<PrDiffFile>();
        foreach (var section in SplitFileSections(unifiedDiff))
        {
            var file = ParseFileSection(section);
            if (file is null)
            {
                continue;
            }

            var component = componentByFile.GetValueOrDefault(file.Path)
                ?? components
                    .FirstOrDefault(c => c.RootPaths.Any(r =>
                        r.Length == 0 || file.Path.StartsWith(r.TrimEnd('/') + "/", StringComparison.Ordinal)))
                    ?.Name;
            var isTest = roles.TryGetValue(file.Path, out var role)
                ? role.HasFlag(FileRole.Test)
                : LooksLikeTestPath(file.Path);

            files.Add(file with { Component = component, IsTest = isTest });
        }

        return files;
    }

    private static IEnumerable<string> SplitFileSections(string diff)
    {
        var sections = FileHeaderPattern().Split(diff);
        // Split keeps the leading prelude at index 0 (empty or stat noise) — skip it.
        return sections.Skip(1);
    }

    private static PrDiffFile? ParseFileSection(string section)
    {
        var lines = section.Split('\n');
        string? newPath = null, oldPath = null;
        var changeKind = "modified";
        var renamed = false;

        foreach (var line in lines.Take(12))
        {
            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                changeKind = "added";
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                changeKind = "deleted";
            }
            else if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                renamed = true;
                newPath = line["rename to ".Length..].Trim();
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                oldPath = line["rename from ".Length..].Trim();
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                newPath = line[6..].Trim();
            }
            else if (line.StartsWith("--- a/", StringComparison.Ordinal) && oldPath is null)
            {
                oldPath = line[6..].Trim();
            }
        }

        if (renamed)
        {
            changeKind = "renamed";
        }

        var path = newPath ?? (changeKind == "deleted" ? oldPath : null);
        if (path is null)
        {
            return null;
        }

        var hunks = new List<PrDiffHunk>();
        var additions = 0;
        var deletions = 0;
        var truncated = false;
        List<string>? current = null;
        var currentStart = 0;
        var currentLines = 0;

        void FlushHunk()
        {
            if (current is null)
            {
                return;
            }

            if (hunks.Count < MaxHunksPerFile)
            {
                var text = current.Count > MaxHunkLines
                    ? string.Join('\n', current.Take(MaxHunkLines)) + "\n…[hunk truncated]"
                    : string.Join('\n', current);
                hunks.Add(new PrDiffHunk(currentStart, currentLines, text));
                truncated |= current.Count > MaxHunkLines;
            }
            else
            {
                truncated = true;
            }

            current = null;
        }

        foreach (var line in lines)
        {
            var hunkMatch = HunkHeaderPattern().Match(line);
            if (hunkMatch.Success)
            {
                FlushHunk();
                currentStart = int.Parse(hunkMatch.Groups[1].Value);
                currentLines = hunkMatch.Groups[2].Success && hunkMatch.Groups[2].Value.Length > 0
                    ? int.Parse(hunkMatch.Groups[2].Value)
                    : 1;
                current = [line.Trim()];
                continue;
            }

            if (current is not null)
            {
                if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
                {
                    additions++;
                    current.Add(line);
                }
                else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
                {
                    deletions++;
                    current.Add(line);
                }
                else if (line.StartsWith(' ') || line.Length == 0)
                {
                    current.Add(line);
                }
            }
        }

        FlushHunk();

        return new PrDiffFile(
            path, changeKind, renamed ? oldPath : null, IsTest: false,
            additions, deletions, Component: null, hunks, truncated);
    }

    private static bool LooksLikeTestPath(string path) =>
        path.Split('/').Any(s => s is "test" or "tests" or "__tests__" or "spec" or "e2e")
        || path.Contains(".test.", StringComparison.OrdinalIgnoreCase)
        || path.Contains(".spec.", StringComparison.OrdinalIgnoreCase)
        || path.Contains("Tests", StringComparison.Ordinal);

    [GeneratedRegex(@"^diff --git .*$\n?", RegexOptions.Multiline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex FileHeaderPattern();

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.None, matchTimeoutMilliseconds: 500)]
    private static partial Regex HunkHeaderPattern();
}
