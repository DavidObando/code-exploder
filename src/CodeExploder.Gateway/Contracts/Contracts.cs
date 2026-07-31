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
    SessionProgress Progress,
    Guid AnalysisId);

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
    string MyState,
    bool HasQuiz,
    int? QuizBestPct);

public sealed record BlockDto(Guid Id, int Ord, string Type, System.Text.Json.JsonElement Data);

public sealed record SectionDetail(
    Guid Id, string Slug, string Title, string Kind, string Status, Guid? QuizId,
    IReadOnlyList<BlockDto> Blocks);

public sealed record QuizChoiceDto(string Key, string Text);

/// <summary>Client-facing question: answer keys and rubrics are stripped server-side.</summary>
public sealed record QuizQuestionDto(
    Guid Id, int Ord, string Type, string Prompt,
    IReadOnlyList<QuizChoiceDto>? Choices, int? MaxWords);

public sealed record QuizDto(Guid Id, Guid SectionId, string Title, IReadOnlyList<QuizQuestionDto> Questions);

public sealed record QuizAnswerDto(Guid QuestionId, List<string>? ChoiceKeys, string? Text);

public sealed record QuizAttemptRequest(List<QuizAnswerDto> Answers);

public sealed record PerQuestionResult(Guid QuestionId, bool? Correct, bool Excluded, string? FeedbackMd);

public sealed record QuizAttemptDto(
    Guid Id, DateTimeOffset SubmittedAt, string Status, int? ScorePct,
    IReadOnlyList<PerQuestionResult> PerQuestion);

public sealed record ProgressUpdateRequest(string State);

public sealed record ProgressUpdateResponse(Guid SectionId, string State, SessionProgress SessionProgress);

public sealed record KbStatus(int EmbeddedChunks, int TotalChunks, bool Ready);

public sealed record ThreadDto(Guid Id, string Title, DateTimeOffset CreatedAt, DateTimeOffset? LastMessageAt);

public sealed record NewThreadRequest(string? Title);

public sealed record QaMessageDto(
    Guid Id, int Ord, string Role, string Content, string Status,
    System.Text.Json.JsonElement? Citations, DateTimeOffset CreatedAt);

public sealed record NewMessageRequest(string Content, Guid? SectionContext);

public sealed record NewMessageResponse(Guid UserMessageId, Guid AssistantMessageId);

public sealed record ChunkPeek(string Path, int StartLine, int EndLine, string Language, string Content);

public sealed record SearchHit(Guid ChunkId, string Path, int StartLine, int EndLine, string Snippet);

public sealed record SearchProse(string Kind, string Title, string Text);

public sealed record SearchResponse(IReadOnlyList<SearchHit> Chunks, IReadOnlyList<SearchProse> Prose);

public sealed record ErrorResponse(string Message);

public sealed record HealthResponse(string Status);

public sealed record ConfigResponse(string AuthMode);

public sealed record MeResponse(string Name, string Subject);

public sealed record QueueStatus(long Depth, long ActiveJobs);

public sealed record SystemStatusResponse(bool Db, QueueStatus Queue);
