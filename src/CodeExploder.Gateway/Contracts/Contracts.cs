namespace CodeExploder.Gateway.Contracts;

public sealed record SessionProgress(int CompletedSections, int TotalSections);

public sealed record SessionSummary(
    Guid Id,
    string Kind,
    string Title,
    string RepoOwner,
    string RepoName,
    int? PrNumber,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    SessionProgress Progress);

public sealed record NewSessionRequest(string Url, string? GitRef);

public sealed record StageInfo(string Key, string Label, string State, double? Percent, string? Detail);

public sealed record NarrationLine(DateTimeOffset At, string Text);

public sealed record AnalysisSnapshot(
    string Status,
    IReadOnlyList<StageInfo> Stages,
    IReadOnlyList<NarrationLine> Narration,
    long LastEventId,
    CodeExploder.Domain.RepoSummary? Summary);

public sealed record ExperienceToc(
    Guid ExperienceId,
    int Version,
    string CommitSha,
    string Model,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SectionTocEntry> Sections);

public sealed record SectionTocEntry(
    Guid Id,
    string Slug,
    string Kind,
    string Title,
    string Summary,
    int Ord,
    int Depth,
    Guid? ParentSectionId,
    int EstimatedMinutes,
    string Status,
    string MyState);

public sealed record BlockDto(Guid Id, int Ord, string Type, System.Text.Json.JsonElement Data);

public sealed record SectionDetail(
    Guid Id, string Slug, string Title, string Kind, string Status, IReadOnlyList<BlockDto> Blocks);

public sealed record ProgressUpdateRequest(string State);

public sealed record ProgressUpdateResponse(Guid SectionId, string State, SessionProgress SessionProgress);

public sealed record ErrorResponse(string Message);

public sealed record HealthResponse(string Status);

public sealed record ConfigResponse(string AuthMode);

public sealed record MeResponse(string Name, string Subject);

public sealed record QueueStatus(long Depth, long ActiveJobs);

public sealed record SystemStatusResponse(bool Db, QueueStatus Queue);
