namespace CodeExploder.Domain;

/// <summary>
/// S4 output: the structured summary of one component (docs/01-analysis-pipeline.md).
/// Schema-validated with one repair retry; stored alongside a short prose summary.
/// </summary>
public sealed record ComponentSummaryDoc(
    string Purpose,
    IReadOnlyList<string> Responsibilities,
    IReadOnlyList<string> KeyTypes,
    IReadOnlyList<string> ExternalDeps,
    IReadOnlyList<TalksTo> TalksTo,
    IReadOnlyList<string> DataFlows,
    IReadOnlyList<NotableFile> NotableFiles,
    IReadOnlyList<string> Risks);

public sealed record TalksTo(string Component, string Via, string Direction);

public sealed record NotableFile(string Path, string Why);

/// <summary>
/// S5 output: the architecture backbone every downstream stage consumes — canonical
/// components, labeled edges, named product scenarios, and a prose overview.
/// </summary>
public sealed record ArchitectureDoc(
    string OverviewMd,
    IReadOnlyList<ArchComponent> Components,
    IReadOnlyList<ArchEdge> Edges,
    IReadOnlyList<ArchScenario> Scenarios);

public sealed record ArchComponent(string Id, string Name, string Purpose, IReadOnlyList<string> KeyFiles);

public sealed record ArchEdge(string From, string To, string Label);

public sealed record ArchScenario(string Id, string Title, string Description, IReadOnlyList<ScenarioStep> Steps);

public sealed record ScenarioStep(string From, string To, string Action);

/// <summary>
/// A diagram content spec (docs/01 §S6a). The LLM emits this JSON — never Mermaid;
/// the deterministic renderer produces syntactically valid Mermaid from it, and the
/// ordered stages drive the progressive-whiteboard reveal (docs/05-ux.md).
/// </summary>
public sealed record DiagramSpec(
    string Kind, // "flowchart" | "sequence"
    string Title,
    IReadOnlyList<DiagramNode> Nodes,
    IReadOnlyList<DiagramEdge> Edges,
    IReadOnlyList<DiagramStage> Stages);

public sealed record DiagramNode(string Id, string Label, string? Group, string? Note);

public sealed record DiagramEdge(string From, string To, string? Label);

/// <summary>Stage k reveals the union of stages 0..k. EdgeIndexes point into Edges.</summary>
public sealed record DiagramStage(
    string Title,
    string NarrationMd,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<int> EdgeIndexes);

public static class SectionKind
{
    public const string Intro = "intro";
    public const string Architecture = "architecture";
    public const string Scenario = "scenario";
    public const string Build = "build";
    public const string Story = "story";
    public const string DeepDive = "deep-dive";
    public const string DeepDiveTour = "deep-dive-tour";
    public const string DeepDiveFlow = "deep-dive-flow";
    public const string DeepDiveInterfaces = "deep-dive-interfaces";
}

/// <summary>Status machine of an explosions row (M10). 'partial' means the join
/// completed with at least one failed child section.</summary>
public static class ExplosionStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Ready = "ready";
    public const string Partial = "partial";
    public const string Failed = "failed";
}

public static class ExplosionTrigger
{
    public const string Eager = "eager";
    public const string OnDemand = "on_demand";
}

public static class SectionState
{
    public const string Pending = "pending";
    public const string Generating = "generating";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public static class BlockType
{
    public const string Markdown = "markdown";
    public const string Diagram = "diagram";
    public const string Code = "code";
    public const string Callout = "callout";
}

public static class ProgressState
{
    public const string Unread = "unread";
    public const string Read = "read";
    public const string Skipped = "skipped";
    public const string Completed = "completed";
}
