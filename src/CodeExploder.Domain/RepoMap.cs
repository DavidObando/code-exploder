namespace CodeExploder.Domain;

/// <summary>
/// The deterministic repo map produced by S1 (docs/01-analysis-pipeline.md). A clean
/// clone contains only tracked files, so .gitignore is inherently applied; the mapper
/// layers the built-in excludes, binary/minified sniffing, and generated-file
/// heuristics on top, keeping excluded files in the map with a reason so the tutorial
/// can honestly mention them.
/// </summary>
public sealed record RepoMap(
    IReadOnlyList<RepoFile> Files,
    IReadOnlyList<LanguageStat> Languages,
    IReadOnlyList<string> BuildSystems,
    IReadOnlyList<string> CiConfigs,
    IReadOnlyList<string> EntryPoints,
    IReadOnlyList<ChurnStat> TopChurnFiles,
    int CommitCount,
    int ContributorCount,
    long TotalBytes)
{
    public int AnalyzedFileCount => Files.Count(f => !f.Excluded);

    public int ExcludedFileCount => Files.Count(f => f.Excluded);
}

public sealed record RepoFile(
    string Path,
    string Language,
    long SizeBytes,
    bool Excluded,
    string? ExcludeReason,
    FileRole Role,
    int Churn,
    int Rank);

/// <summary>What a file is, for ranking and narration. Flags are not mutually exclusive.</summary>
[Flags]
public enum FileRole
{
    None = 0,
    EntryPoint = 1,
    Test = 2,
    Doc = 4,
    Manifest = 8,
    Ci = 16,
}

public sealed record LanguageStat(string Name, int Files, long Bytes, double Percent);

public sealed record ChurnStat(string Path, int Commits);

/// <summary>Git history facts gathered by the acquire stage and consumed by the mapper.</summary>
public sealed record GitStats(
    int CommitCount,
    int ContributorCount,
    IReadOnlyDictionary<string, int> ChurnByPath);

/// <summary>A chunk of a file sized for retrieval (S2; ~2–4k tokens, docs/01 §S2).</summary>
public sealed record CodeChunk(string FilePath, int StartLine, int EndLine, string Content, int TokenCount);

/// <summary>A detected component (S3): manifest boundaries where they exist, else directories.</summary>
public sealed record Component(
    string Name,
    IReadOnlyList<string> RootPaths,
    IReadOnlyList<string> FilePaths,
    IReadOnlyList<string> TopFiles);

/// <summary>
/// The repo vitals the UI shows when analysis is ready; persisted inside
/// analyses.plan. Field names are part of the SPA contract (docs/04-api.md).
/// </summary>
public sealed record RepoSummary(
    string CommitSha,
    string? Description,
    int FileCount,
    int AnalyzedFileCount,
    int ExcludedFileCount,
    int ChunkCount,
    long TotalBytes,
    IReadOnlyList<LanguageStat> Languages,
    IReadOnlyList<string> BuildSystems,
    IReadOnlyList<string> CiConfigs,
    IReadOnlyList<string> EntryPoints,
    IReadOnlyList<ComponentSummary> Components,
    IReadOnlyList<ChurnStat> TopChurnFiles,
    int CommitCount,
    int ContributorCount);

public sealed record ComponentSummary(string Name, int FileCount, IReadOnlyList<string> TopFiles);
