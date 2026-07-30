using CodeExploder.Domain;

namespace CodeExploder.Analysis;

/// <summary>
/// S3 plan step: assembles the repo vitals summary from the deterministic analysis
/// outputs (docs/01 §S3). Persisted inside analyses.plan and shown by the UI.
/// </summary>
public static class RunPlanner
{
    public static RepoSummary BuildSummary(
        string commitSha,
        string? description,
        RepoMap map,
        IReadOnlyList<Component> components,
        int chunkCount)
    {
        ArgumentNullException.ThrowIfNull(commitSha);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(components);

        return new RepoSummary(
            commitSha,
            description,
            map.Files.Count,
            map.AnalyzedFileCount,
            map.ExcludedFileCount,
            chunkCount,
            map.TotalBytes,
            map.Languages,
            map.BuildSystems,
            map.CiConfigs,
            map.EntryPoints,
            components.Select(c => new ComponentSummary(c.Name, c.FilePaths.Count, c.TopFiles)).ToList(),
            map.TopChurnFiles,
            map.CommitCount,
            map.ContributorCount);
    }
}
