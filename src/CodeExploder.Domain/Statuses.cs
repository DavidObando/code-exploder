namespace CodeExploder.Domain;

/// <summary>Session lifecycle as shown in the left-pane history (docs/03-data-model.md).</summary>
public static class SessionStatus
{
    public const string Queued = "queued";
    public const string Analyzing = "analyzing";
    public const string Ready = "ready";
    public const string Partial = "partial";
    public const string Failed = "failed";
}

public static class AnalysisStatus
{
    public const string Planning = "planning";
    public const string Running = "running";
    public const string Ready = "ready";
    public const string ReadyWithWarnings = "ready_with_warnings";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class SessionKind
{
    public const string Repo = "repo";
    public const string Pr = "pr";
}
