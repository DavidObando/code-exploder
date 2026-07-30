namespace CodeExploder.Domain;

public sealed record Repo(Guid Id, string Owner, string Name, string Url, DateTimeOffset CreatedAt);

public sealed record Analysis(
    Guid Id,
    Guid RepoId,
    string Kind,
    int? PrNumber,
    string Status,
    int SectionsTotal,
    int SectionsReady,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt);

/// <summary>A session joined with its repo and analysis — the shape the UI consumes.</summary>
public sealed record SessionView(
    Guid Id,
    string Kind,
    string Title,
    string RepoOwner,
    string RepoName,
    int? PrNumber,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    Guid AnalysisId,
    int SectionsTotal,
    int SectionsReady);
