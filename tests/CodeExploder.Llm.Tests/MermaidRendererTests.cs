using CodeExploder.Domain;
using Xunit;

namespace CodeExploder.Llm.Tests;

public sealed class MermaidRendererTests
{
    [Fact]
    public void FlowchartRendersGroupsAsSubgraphsAndEdgesWithAndWithoutLabels()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "Architecture",
            [
                new DiagramNode("gw", "Gateway", null, null),
                new DiagramNode("api", "API", "Backend", null),
                new DiagramNode("db", "Database", "Backend", null),
            ],
            [
                new DiagramEdge("gw", "api", "HTTP"),
                new DiagramEdge("api", "db", null),
            ],
            [new DiagramStage("All", "", ["gw", "api", "db"], [0, 1])]);

        var rendered = MermaidRenderer.Render(spec);

        Assert.Equal(
            "flowchart TD\n" +
            "  gw[\"Gateway\"]\n" +
            "  subgraph Backend\n" +
            "    api[\"API\"]\n" +
            "    db[\"Database\"]\n" +
            "  end\n" +
            "  gw -->|HTTP| api\n" +
            "  api --> db",
            rendered.Mermaid);
    }

    [Fact]
    public void LabelsAreSanitizedAndCapped()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [new DiagramNode("a", "Say \"hi\" [now]\nplease | ok `x`", null, null), new DiagramNode("b", new string('z', 100), null, null)],
            [new DiagramEdge("a", "b", "uses\n\"quotes\"")],
            [new DiagramStage("S", "", ["a", "b"], [0])]);

        var rendered = MermaidRenderer.Render(spec);

        Assert.Contains("a[\"Say hi now please ok x\"]", rendered.Mermaid, StringComparison.Ordinal);
        Assert.Contains("b[\"" + new string('z', 57) + "...\"]", rendered.Mermaid, StringComparison.Ordinal);
        Assert.Contains("a -->|uses quotes| b", rendered.Mermaid, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hi\"", rendered.Mermaid, StringComparison.Ordinal);
    }

    [Fact]
    public void IdsAreNormalizedAndEdgesAndStagesRewritten()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [
                new DiagramNode("web ui", "Web UI", null, null),
                new DiagramNode("web-ui", "Other UI", null, null),
                new DiagramNode("api!", "API", null, null),
            ],
            [new DiagramEdge("web ui", "api!", null), new DiagramEdge("web-ui", "api!", null)],
            [new DiagramStage("S", "", ["web ui", "web-ui", "api!"], [0, 1])]);

        var rendered = MermaidRenderer.Render(spec);
        var normalized = rendered.NormalizedSpec;

        Assert.Equal(["web_ui", "web_ui_2", "api_"], normalized.Nodes.Select(n => n.Id));
        Assert.Equal("web_ui", normalized.Edges[0].From);
        Assert.Equal("api_", normalized.Edges[0].To);
        Assert.Equal("web_ui_2", normalized.Edges[1].From);
        Assert.Equal(["web_ui", "web_ui_2", "api_"], normalized.Stages[0].NodeIds);
        Assert.Equal([0, 1], normalized.Stages[0].EdgeIndexes);
        Assert.Contains("web_ui --> api_", rendered.Mermaid, StringComparison.Ordinal);
        Assert.Contains("web_ui_2 --> api_", rendered.Mermaid, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEdgeEndpointsAreDroppedAndStageIndexesRemapped()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [new DiagramNode("a", "A", null, null), new DiagramNode("b", "B", null, null)],
            [
                new DiagramEdge("ghost", "a", null), // dropped (1 of 3 — under the >50% limit)
                new DiagramEdge("a", "b", "kept"),
                new DiagramEdge("b", "a", null),
            ],
            [new DiagramStage("S", "", ["a", "b"], [0, 1, 2, 99])]);

        var rendered = MermaidRenderer.Render(spec);
        var normalized = rendered.NormalizedSpec;

        Assert.Equal(2, normalized.Edges.Count);
        Assert.Equal("kept", normalized.Edges[0].Label);
        Assert.Equal([0, 1], normalized.Stages[0].EdgeIndexes);
        Assert.DoesNotContain("ghost", rendered.Mermaid, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreThanHalfDroppedEdgesThrows()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [new DiagramNode("a", "A", null, null)],
            [
                new DiagramEdge("a", "ghost1", null),
                new DiagramEdge("ghost2", "a", null),
                new DiagramEdge("a", "a", null),
            ],
            [new DiagramStage("S", "", ["a"], [])]);

        var ex = Assert.Throws<DiagramValidationException>(() => MermaidRenderer.Render(spec));
        Assert.Contains("2 of 3 edges", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactlyHalfDroppedEdgesDoesNotThrow()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [new DiagramNode("a", "A", null, null), new DiagramNode("b", "B", null, null)],
            [new DiagramEdge("a", "b", null), new DiagramEdge("a", "ghost", null)],
            [new DiagramStage("S", "", ["a", "b"], [0])]);

        var rendered = MermaidRenderer.Render(spec);

        Assert.Single(rendered.NormalizedSpec.Edges);
    }

    [Fact]
    public void UnknownStageNodeIdsAreDroppedWithoutThrowing()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [new DiagramNode("a", "A", null, null)],
            [],
            [new DiagramStage("S", "", ["a", "nope"], [5])]);

        var rendered = MermaidRenderer.Render(spec);

        Assert.Equal(["a"], rendered.NormalizedSpec.Stages[0].NodeIds);
        Assert.Empty(rendered.NormalizedSpec.Stages[0].EdgeIndexes);
    }

    [Fact]
    public void EmptyStagesSynthesizeSingleStageRevealingEverything()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "My Diagram",
            [new DiagramNode("a", "A", null, null), new DiagramNode("b", "B", null, null)],
            [new DiagramEdge("a", "b", null)],
            []);

        var rendered = MermaidRenderer.Render(spec);
        var stage = Assert.Single(rendered.NormalizedSpec.Stages);

        Assert.Equal("My Diagram", stage.Title);
        Assert.Equal(string.Empty, stage.NarrationMd);
        Assert.Equal(["a", "b"], stage.NodeIds);
        Assert.Equal([0], stage.EdgeIndexes);
    }

    [Fact]
    public void SequenceRendersParticipantsInNodeOrderAndDefaultsMessageLabel()
    {
        var spec = new DiagramSpec(
            "sequence",
            "Login flow",
            [
                new DiagramNode("ui", "Web UI", null, null),
                new DiagramNode("api", "API", null, null),
            ],
            [
                new DiagramEdge("ui", "api", "POST /login"),
                new DiagramEdge("api", "ui", null),
            ],
            [new DiagramStage("S", "", ["ui", "api"], [0, 1])]);

        var rendered = MermaidRenderer.Render(spec);

        Assert.Equal(
            "sequenceDiagram\n" +
            "  participant ui as Web UI\n" +
            "  participant api as API\n" +
            "  ui->>api: POST /login\n" +
            "  api->>ui: calls",
            rendered.Mermaid);
    }

    [Fact]
    public void InvalidSpecThrowsListingEveryProblem()
    {
        var spec = new DiagramSpec("pie", "T", [], [], []);

        var ex = Assert.Throws<DiagramValidationException>(() => MermaidRenderer.Render(spec));

        Assert.Contains("unknown diagram kind 'pie'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no nodes", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.Problems.Count);
    }

    [Fact]
    public void DuplicateNodeIdsThrow()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [new DiagramNode("a", "A", null, null), new DiagramNode("a", "A again", null, null)],
            [],
            []);

        var ex = Assert.Throws<DiagramValidationException>(() => MermaidRenderer.Render(spec));
        Assert.Contains("duplicate node id 'a'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [
                new DiagramNode("a b", "A \"B\"", "Group One", null),
                new DiagramNode("c", "C", null, null),
            ],
            [new DiagramEdge("a b", "c", "x|y")],
            []);

        var first = MermaidRenderer.Render(spec);
        var second = MermaidRenderer.Render(spec);

        Assert.Equal(first.Mermaid, second.Mermaid);
        Assert.Equal(
            first.NormalizedSpec.Nodes.Select(n => n.Id),
            second.NormalizedSpec.Nodes.Select(n => n.Id));
    }

    [Fact]
    public void NoLineHasTrailingWhitespace()
    {
        var spec = new DiagramSpec(
            "flowchart",
            "T",
            [new DiagramNode("a", "A", "G", null), new DiagramNode("b", "B", null, null)],
            [new DiagramEdge("a", "b", "label")],
            []);

        var rendered = MermaidRenderer.Render(spec);

        foreach (var line in rendered.Mermaid.Split('\n'))
        {
            Assert.Equal(line.TrimEnd(), line);
        }
    }
}
