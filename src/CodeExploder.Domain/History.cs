namespace CodeExploder.Domain;

/// <summary>
/// The deterministic history mine (docs/08 §M9): the repository's life, segmented
/// into eras, with component births, key moments, and the contributor cast — the raw
/// material the storyteller narrates. Stored as a workspace artifact (history.json).
/// </summary>
public sealed record HistoryDoc(
    int TotalCommits,
    bool Truncated,
    DateTimeOffset FirstCommitAt,
    DateTimeOffset LastCommitAt,
    IReadOnlyList<Contributor> Contributors,
    IReadOnlyList<HistoryEra> Eras);

public sealed record Contributor(string Name, int Commits);

public sealed record HistoryEra(
    int Index,
    DateTimeOffset Start,
    DateTimeOffset End,
    int CommitCount,
    IReadOnlyList<string> TopAuthors,
    int FilesTouched,
    IReadOnlyList<string> ComponentsBorn,
    IReadOnlyList<HistoryMoment> Moments);

/// <summary>A notable commit: first-ever, first test, first CI, era's biggest change, a component's birth.</summary>
public sealed record HistoryMoment(
    string Kind, // "first-commit" | "first-test" | "first-ci" | "biggest-change" | "component-born"
    string ShaShort,
    DateTimeOffset At,
    string Author,
    string Subject,
    int FilesTouched,
    string? Detail);

/// <summary>One parsed `git log` entry (input to the miner).</summary>
public sealed record HistoryCommit(
    string Sha,
    string Author,
    DateTimeOffset At,
    string Subject,
    IReadOnlyList<string> Paths);
