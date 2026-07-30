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
    long LastEventId);

public sealed record ErrorResponse(string Message);

public sealed record HealthResponse(string Status);

public sealed record ConfigResponse(string AuthMode);

public sealed record MeResponse(string Name, string Subject);

public sealed record QueueStatus(long Depth, long ActiveJobs);

public sealed record SystemStatusResponse(bool Db, QueueStatus Queue);
