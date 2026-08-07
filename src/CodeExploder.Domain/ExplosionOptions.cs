namespace CodeExploder.Domain;

/// <summary>
/// Guard rails for scope explosion (deep dives, M10). Bound from the "Explosions"
/// config section by each host that participates (gateway + both workers).
/// </summary>
public sealed class ExplosionOptions
{
    /// <summary>Maximum explosion nesting (1 = top-level component dive).</summary>
    public int MaxDepth { get; set; } = 3;

    /// <summary>How many top-ranked components to explode automatically after finalize.</summary>
    public int EagerTopK { get; set; } = 2;

    /// <summary>Scopes smaller than this aren't worth a dive and aren't offered one.</summary>
    public int MinScopeFiles { get; set; } = 8;

    /// <summary>Cap on sub-components detected within a scope.</summary>
    public int MaxSubComponents { get; set; } = 8;

    /// <summary>Cap on child content sections planned per dive.</summary>
    public int MaxChildSections { get; set; } = 5;

    /// <summary>Queued/running explosions allowed per analysis at once.</summary>
    public int MaxActivePerAnalysis { get; set; } = 1;

    /// <summary>Priority for user-requested dives; 5 outranks fresh-session work (0).
    /// Set to 0 for strict FIFO fairness with new sessions.</summary>
    public int OnDemandPriority { get; set; } = LlmJobTypes.OnDemandExplodePriority;
}
