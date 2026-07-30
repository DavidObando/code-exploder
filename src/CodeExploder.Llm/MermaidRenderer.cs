using System.Text;
using CodeExploder.Domain;

namespace CodeExploder.Llm;

/// <summary>The deterministic Mermaid text plus the normalized spec it was rendered from.</summary>
public sealed record RenderedDiagram(string Mermaid, DiagramSpec NormalizedSpec);

/// <summary>Thrown when a <see cref="DiagramSpec"/> is invalid; lists every problem found.</summary>
public sealed class DiagramValidationException : Exception
{
    public DiagramValidationException()
    {
    }

    public DiagramValidationException(string message)
        : base(message)
    {
    }

    public DiagramValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DiagramValidationException(IReadOnlyList<string> problems)
        : base("Invalid diagram spec: " + string.Join("; ", problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<string> Problems { get; } = [];
}

/// <summary>
/// Deterministic DiagramSpec → Mermaid renderer (docs/01 §S6a). The LLM emits the
/// spec JSON — never Mermaid; this renderer guarantees syntactically valid output
/// and normalizes ids/labels so the progressive-whiteboard stages stay consistent.
/// </summary>
public static class MermaidRenderer
{
    private const int MaxLabelLength = 60;

    public static RenderedDiagram Render(DiagramSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var problems = new List<string>();

        if (spec.Kind is not ("flowchart" or "sequence"))
        {
            problems.Add($"unknown diagram kind '{spec.Kind}' (expected 'flowchart' or 'sequence')");
        }

        var nodes = spec.Nodes ?? [];
        var edges = spec.Edges ?? [];
        var stages = spec.Stages ?? [];

        if (nodes.Count == 0)
        {
            problems.Add("diagram has no nodes");
        }

        // Normalize node ids to [A-Za-z0-9_], deduping collisions with numeric suffixes.
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedSafeIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedNodes = new List<DiagramNode>(nodes.Count);
        foreach (var node in nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
            {
                problems.Add("node with empty id");
                continue;
            }

            if (idMap.ContainsKey(node.Id))
            {
                problems.Add($"duplicate node id '{node.Id}'");
                continue;
            }

            var safeId = SanitizeId(node.Id);
            if (!usedSafeIds.Add(safeId))
            {
                var suffix = 2;
                while (!usedSafeIds.Add(safeId + "_" + suffix))
                {
                    suffix++;
                }

                safeId = safeId + "_" + suffix;
            }

            idMap[node.Id] = safeId;
            normalizedNodes.Add(node with { Id = safeId });
        }

        // Rewrite edges to safe ids; drop edges whose endpoints are unknown.
        var normalizedEdges = new List<DiagramEdge>();
        var edgeIndexMap = new Dictionary<int, int>(); // original index -> normalized index
        var droppedEdges = 0;
        for (var i = 0; i < edges.Count; i++)
        {
            var edge = edges[i];
            if (edge.From is not null && edge.To is not null
                && idMap.TryGetValue(edge.From, out var from)
                && idMap.TryGetValue(edge.To, out var to))
            {
                edgeIndexMap[i] = normalizedEdges.Count;
                normalizedEdges.Add(edge with { From = from, To = to });
            }
            else
            {
                droppedEdges++;
            }
        }

        if (edges.Count > 0 && droppedEdges * 2 > edges.Count)
        {
            problems.Add($"{droppedEdges} of {edges.Count} edges reference unknown nodes (more than half)");
        }

        // Rewrite stages: drop unknown node ids and out-of-range/dropped edge indexes.
        var normalizedStages = new List<DiagramStage>(stages.Count);
        foreach (var stage in stages)
        {
            var nodeIds = (stage.NodeIds ?? [])
                .Where(id => id is not null && idMap.ContainsKey(id))
                .Select(id => idMap[id])
                .ToList();
            var edgeIndexes = (stage.EdgeIndexes ?? [])
                .Where(edgeIndexMap.ContainsKey)
                .Select(index => edgeIndexMap[index])
                .ToList();
            normalizedStages.Add(stage with { NodeIds = nodeIds, EdgeIndexes = edgeIndexes });
        }

        if (normalizedStages.Count == 0)
        {
            normalizedStages.Add(new DiagramStage(
                spec.Title,
                string.Empty,
                normalizedNodes.Select(n => n.Id).ToList(),
                Enumerable.Range(0, normalizedEdges.Count).ToList()));
        }

        if (problems.Count > 0)
        {
            throw new DiagramValidationException(problems);
        }

        var normalizedSpec = spec with { Nodes = normalizedNodes, Edges = normalizedEdges, Stages = normalizedStages };
        var mermaid = normalizedSpec.Kind == "sequence"
            ? RenderSequence(normalizedSpec)
            : RenderFlowchart(normalizedSpec);
        return new RenderedDiagram(mermaid, normalizedSpec);
    }

    private static string RenderFlowchart(DiagramSpec spec)
    {
        var lines = new List<string> { "flowchart TD" };

        foreach (var node in spec.Nodes.Where(n => string.IsNullOrWhiteSpace(n.Group)))
        {
            lines.Add("  " + NodeLine(node));
        }

        var groups = spec.Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.Group))
            .Select(n => n.Group!)
            .Distinct(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var groupName = SanitizeLabel(group);
            lines.Add("  subgraph " + (groupName.Length > 0 ? groupName : "group"));
            foreach (var node in spec.Nodes.Where(n => string.Equals(n.Group, group, StringComparison.Ordinal)))
            {
                lines.Add("    " + NodeLine(node));
            }

            lines.Add("  end");
        }

        foreach (var edge in spec.Edges)
        {
            var label = SanitizeLabel(edge.Label);
            lines.Add(label.Length > 0
                ? $"  {edge.From} -->|{label}| {edge.To}"
                : $"  {edge.From} --> {edge.To}");
        }

        return string.Join('\n', lines);
    }

    private static string RenderSequence(DiagramSpec spec)
    {
        var lines = new List<string> { "sequenceDiagram" };

        foreach (var node in spec.Nodes)
        {
            var label = SanitizeLabel(node.Label);
            lines.Add($"  participant {node.Id} as {(label.Length > 0 ? label : node.Id)}");
        }

        foreach (var edge in spec.Edges)
        {
            var label = SanitizeLabel(edge.Label);
            lines.Add($"  {edge.From}->>{edge.To}: {(label.Length > 0 ? label : "calls")}");
        }

        return string.Join('\n', lines);
    }

    private static string NodeLine(DiagramNode node)
    {
        var label = SanitizeLabel(node.Label);
        return $"{node.Id}[\"{(label.Length > 0 ? label : node.Id)}\"]";
    }

    private static string SanitizeId(string id)
    {
        var sb = new StringBuilder(id.Length);
        foreach (var ch in id.Trim())
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        return sb.ToString();
    }

    /// <summary>Replaces Mermaid-breaking characters with spaces, collapses runs, caps at 60 chars.</summary>
    private static string SanitizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(label.Length);
        foreach (var ch in label)
        {
            sb.Append(ch is '"' or '[' or ']' or '|' or '`' or '\r' or '\n' or '\t' ? ' ' : ch);
        }

        var text = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > MaxLabelLength)
        {
            text = text[..(MaxLabelLength - 3)] + "...";
        }

        return text;
    }
}
