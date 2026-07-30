namespace CodeExploder.Domain;

/// <summary>
/// The fixed stage list the UI's progress view renders (docs/05-ux.md). The M0 noop
/// pipeline walks a subset of the real pipeline's stages; M1+ adds the rest without
/// changing the contract shape.
/// </summary>
public static class AnalysisStages
{
    public const string Clone = "clone";
    public const string Map = "map";
    public const string Index = "index";
    public const string Plan = "plan";
    public const string Summarize = "summarize";
    public const string Synthesize = "synthesize";
    public const string Sections = "sections";
    public const string Finalize = "finalize";

    /// <summary>Stage keys in pipeline order, with UI labels (M2: S0–S6 + finalize).</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> All =
    [
        (Clone, "Fetch & clone"),
        (Map, "Map repository"),
        (Index, "Index & chunk"),
        (Plan, "Plan analysis"),
        (Summarize, "Summarize components"),
        (Synthesize, "Synthesize architecture"),
        (Sections, "Write sections"),
        (Finalize, "Finalize"),
    ];
}

public static class StageState
{
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>Job types on the gpu-gen lane (docs/02-queue-and-events.md).</summary>
public static class LlmJobTypes
{
    public const string SummarizeComponent = "summarize-component";
    public const string Synthesize = "synthesize";
    public const string TutorialSection = "tutorial-section";
    public const string FinalizeExperience = "finalize-experience";
}

/// <summary>
/// Event kinds carried in the session-event envelope (docs/04-api.md). Lifecycle kinds
/// invalidate client queries; stream kinds render directly.
/// </summary>
public static class SessionEventKinds
{
    public const string AnalysisStageChanged = "AnalysisStageChanged";
    public const string AnalysisProgress = "AnalysisProgress";
    public const string AnalysisNarration = "AnalysisNarration";
    public const string SectionReady = "SectionReady";
    public const string AnalysisCompleted = "AnalysisCompleted";
    public const string AnalysisFailed = "AnalysisFailed";
}
