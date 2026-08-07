using System.Text.Json;
using CodeExploder.Domain;

namespace CodeExploder.Analysis;

/// <summary>
/// M10: ranks components by how much a deep dive is worth doing eagerly. Fan-in comes
/// from ComponentSummaryDoc.TalksTo (validated against real component names by the
/// summarizer, so hallucination-safe); risk count from the same doc; size and churn
/// share are capped so one huge directory can't drown the signal.
/// </summary>
public static class CriticalityScorer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Returns eligible components (>= minScopeFiles files) ranked by descending score.
    /// <paramref name="summariesByComponent"/> maps component name → structured
    /// ComponentSummaryDoc JSON (null/absent entries tolerated: that term scores 0).
    /// </summary>
    public static IReadOnlyList<(string ComponentName, double Score)> Rank(
        IReadOnlyList<Component> components,
        RepoMap map,
        IReadOnlyDictionary<string, string?> summariesByComponent,
        int minScopeFiles)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(summariesByComponent);

        var docs = new Dictionary<string, ComponentSummaryDoc>(StringComparer.Ordinal);
        foreach (var (name, json) in summariesByComponent)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                var doc = JsonSerializer.Deserialize<ComponentSummaryDoc>(json, JsonOpts);
                if (doc is not null)
                {
                    docs[name] = doc;
                }
            }
            catch (JsonException)
            {
                // A malformed stored summary just contributes nothing to the score.
            }
        }

        var fanIn = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (owner, doc) in docs)
        {
            foreach (var talksTo in doc.TalksTo)
            {
                if (!string.Equals(talksTo.Component, owner, StringComparison.Ordinal))
                {
                    fanIn[talksTo.Component] = fanIn.GetValueOrDefault(talksTo.Component) + 1;
                }
            }
        }

        var churnByPath = map.Files.ToDictionary(f => f.Path, f => f.Churn, StringComparer.Ordinal);
        double totalChurn = map.Files.Sum(f => (double)f.Churn);

        var ranked = new List<(string ComponentName, double Score)>();
        foreach (var component in components)
        {
            if (component.FilePaths.Count < minScopeFiles)
            {
                continue;
            }

            var risks = docs.TryGetValue(component.Name, out var doc) ? doc.Risks.Count : 0;
            var churn = component.FilePaths.Sum(p => (double)churnByPath.GetValueOrDefault(p));
            var churnShare = totalChurn > 0 ? churn / totalChurn : 0;

            var score = 3.0 * fanIn.GetValueOrDefault(component.Name)
                + 2.0 * risks
                + Math.Min(component.FilePaths.Count / 25.0, 4.0)
                + Math.Min(churnShare * 10.0, 4.0);
            ranked.Add((component.Name, score));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.ComponentName, StringComparer.Ordinal)
            .ToList();
    }
}
