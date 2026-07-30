namespace CodeExploder.Domain;

/// <summary>
/// The deterministic PR diff map (docs/01 §PR-diff mode): what changed, classified and
/// mapped onto components, plus the PR metadata the narrative stages consume. Stored
/// as a workspace artifact next to the repo map.
/// </summary>
public sealed record PrDiff(
    int PrNumber,
    string? Title,
    string? Body,
    string BaseRef,
    IReadOnlyList<PrDiffFile> Files)
{
    public IReadOnlyList<string> TouchedComponents =>
        Files.Where(f => f.Component is not null).Select(f => f.Component!)
            .Distinct(StringComparer.Ordinal).ToList();

    public int TotalAdditions => Files.Sum(f => f.Additions);

    public int TotalDeletions => Files.Sum(f => f.Deletions);
}

public sealed record PrDiffFile(
    string Path,
    string ChangeKind, // "added" | "modified" | "deleted" | "renamed"
    string? OldPath,   // renames only
    bool IsTest,
    int Additions,
    int Deletions,
    string? Component,
    IReadOnlyList<PrDiffHunk> Hunks,
    bool HunksTruncated);

/// <summary>One unified-diff hunk; NewStart/NewLines locate it in the head tree.</summary>
public sealed record PrDiffHunk(int NewStart, int NewLines, string Text);

public static class PrSectionKind
{
    public const string Overview = "pr-overview";
    public const string Walkthrough = "pr-walkthrough";
    public const string Risk = "pr-risk";
}
