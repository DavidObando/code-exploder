using CodeExploder.Domain;

namespace CodeExploder.Pipeline;

/// <summary>Deterministic PR-mode helpers (docs/01 §PR-diff mode): no LLM involved.</summary>
public static class PrSupport
{
    /// <summary>
    /// Orders changed components bottom-up for the walkthrough: if A → B (A talks to
    /// B), B is lower-level and reviewed first. Best-effort name matching against the
    /// architecture's canonicalized components; unmatched keep their input order.
    /// </summary>
    public static IReadOnlyList<string> OrderBottomUp(
        IReadOnlyList<string> changedComponents, ArchitectureDoc architecture)
    {
        var idByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in architecture.Components)
        {
            idByName[component.Name] = component.Id;
            idByName[component.Id] = component.Id;
        }

        string? Resolve(string name) =>
            idByName.TryGetValue(name, out var id)
                ? id
                : idByName.FirstOrDefault(kv => kv.Key.Contains(name, StringComparison.OrdinalIgnoreCase)).Value;

        var archIds = changedComponents.ToDictionary(c => c, Resolve, StringComparer.Ordinal);
        var remaining = new List<string>(changedComponents);
        var ordered = new List<string>();

        while (remaining.Count > 0)
        {
            // Pick a component with no outgoing edge to another remaining component
            // (i.e., depends on nothing left) — that's the lowest level.
            var pick = remaining.FirstOrDefault(c =>
            {
                var id = archIds[c];
                if (id is null)
                {
                    return false;
                }

                return !architecture.Edges.Any(e =>
                    e.From == id && remaining.Any(o => o != c && archIds[o] == e.To));
            }) ?? remaining[0];

            ordered.Add(pick);
            remaining.Remove(pick);
        }

        return ordered;
    }

    /// <summary>
    /// Badges an architecture diagram spec for PR mode: changed components get a "✱"
    /// label suffix and a final "What this PR touches" stage carries the change
    /// narration. Purely deterministic decoration of an already-valid spec.
    /// </summary>
    public static DiagramSpec BadgeChangedComponents(
        DiagramSpec spec, IReadOnlyList<string> changedComponents)
    {
        bool IsChanged(DiagramNode node) => changedComponents.Any(c =>
            node.Label.Contains(c, StringComparison.OrdinalIgnoreCase)
            || node.Id.Contains(Slug(c), StringComparison.OrdinalIgnoreCase)
            || c.Contains(node.Label, StringComparison.OrdinalIgnoreCase));

        var changedIds = spec.Nodes.Where(IsChanged).Select(n => n.Id).ToList();
        if (changedIds.Count == 0)
        {
            return spec;
        }

        var nodes = spec.Nodes
            .Select(n => changedIds.Contains(n.Id) ? n with { Label = n.Label + " ✱" } : n)
            .ToList();
        var stages = spec.Stages.Append(new DiagramStage(
            "What this PR touches",
            "The starred components are the ones this pull request changes — keep the rest of the picture in mind as the context they plug into.",
            [], [])).ToList();
        return spec with { Nodes = nodes, Stages = stages };
    }

    private static string Slug(string s) =>
        new(s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());
}
