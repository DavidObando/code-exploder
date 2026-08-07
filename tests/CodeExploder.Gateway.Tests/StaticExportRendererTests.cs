using System.Text.Json;
using CodeExploder.Gateway.Export;
using Xunit;
using R = CodeExploder.Gateway.Export.StaticExportRenderer;

namespace CodeExploder.Gateway.Tests;

/// <summary>
/// The server export renderer mirrors the client's staticExport.ts: surgical HTML
/// escaping, line-numbered code with diff tinting + SHA-pinned links, nested TOC, and
/// a fully self-contained document. Diagrams degrade to a no-JS walkthrough + source.
/// </summary>
public sealed class StaticExportRendererTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly R.GithubContext Github = new("octo", "demo", "abcdef1234567890");

    private static R.ExportBlock Block(string type, object data) =>
        new(type, JsonSerializer.Serialize(data, Json));

    [Fact]
    public void EscapesHtmlSignificantCharacters()
    {
        Assert.Equal("&lt;script&gt;&quot;a&quot;&amp;&#39;b&#39;", R.EscapeHtml("<script>\"a\"&'b'"));
    }

    [Fact]
    public void CodeBlockNumbersLinesEscapesAndLinks()
    {
        var data = new R.CodeDto("src/Binder.cs", 17, 19, "C#", "namespace X;\nclass Binder\na < b", null);
        var html = R.CodeBlockHtml(data, Github);

        Assert.Contains(">17<", html, StringComparison.Ordinal);
        Assert.Contains(">18<", html, StringComparison.Ordinal);
        Assert.Contains(">19<", html, StringComparison.Ordinal);
        Assert.Contains("a &lt; b", html, StringComparison.Ordinal);
        Assert.Contains("https://github.com/octo/demo/blob/abcdef1234567890/src/Binder.cs#L17-L19", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeBlockOmitsLinkWithoutGithub()
    {
        var data = new R.CodeDto("a.ts", 1, 2, "TS", "x\ny", null);
        Assert.DoesNotContain("View on GitHub", R.CodeBlockHtml(data, null), StringComparison.Ordinal);
    }

    [Fact]
    public void CodeBlockTintsDiffLines()
    {
        var data = new R.CodeDto("a.ts", 1, 3, "Diff", "@@ -1 +1 @@\n-old\n+new", null);
        var html = R.CodeBlockHtml(data, Github);
        Assert.Contains("class=\"line hunk\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"line removed\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"line added\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void CalloutRendersVariantAccentTitleAndBody()
    {
        var html = R.CalloutHtml(new R.CalloutDto("warning", "Careful <x>", "ignored"), "<p>body</p>");
        Assert.Contains("Careful &lt;x&gt;", html, StringComparison.Ordinal);
        Assert.Contains("var(--x-warning)", html, StringComparison.Ordinal);
        Assert.Contains("<p>body</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagramShowsWalkthroughAndCollapsibleSource()
    {
        var d = new R.DiagramDto("flowchart", "Flow", "flowchart TD\n  a-->b",
            [new R.DiagramStageDto("One", ""), new R.DiagramStageDto("Two", "")]);
        var html = R.DiagramHtml(d, ["<p>first</p>", "<p>second</p>"]);

        Assert.Contains("One", html, StringComparison.Ordinal);
        Assert.Contains("<p>first</p>", html, StringComparison.Ordinal);
        Assert.Contains("Diagram source (Mermaid)", html, StringComparison.Ordinal);
        Assert.Contains("flowchart TD", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", html, StringComparison.Ordinal); // server never renders SVG
    }

    [Fact]
    public void RenderMarkdownProducesGfmAndEscapesRawHtml()
    {
        Assert.Contains("<p>", R.RenderMarkdown("hello **world**"), StringComparison.Ordinal);
        Assert.Contains("<strong>world</strong>", R.RenderMarkdown("hello **world**"), StringComparison.Ordinal);
        Assert.Contains("<table", R.RenderMarkdown("| a | b |\n|---|---|\n| 1 | 2 |"), StringComparison.Ordinal);
        // DisableHtml: raw HTML in markdown is escaped, never passed through.
        var rendered = R.RenderMarkdown("text <script>alert(1)</script>");
        Assert.DoesNotContain("<script>", rendered, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionHeadingLevelFollowsDepthAndAnchorsSlug()
    {
        Assert.Contains("<h2", R.SectionHtml("intro", "Intro", "intro", 0, ["<p>a</p>"]), StringComparison.Ordinal);
        Assert.Contains("<h4", R.SectionHtml("t", "Deep", "deep-dive-tour", 2, []), StringComparison.Ordinal);
        Assert.Contains("<h6", R.SectionHtml("s", "Deep", "x", 9, []), StringComparison.Ordinal); // capped
        Assert.Contains($"id=\"{R.SectionAnchorId("intro")}\"", R.SectionHtml("intro", "Intro", "intro", 0, []), StringComparison.Ordinal);
    }

    [Fact]
    public void TocNestsNodesAndLinksAnchors()
    {
        var nodes = new List<R.TocNode>
        {
            new("arch", "Arch", [new("dd-core", "Deep dive", [new("dd-core-tour", "Tour", [])])]),
        };
        var html = R.TocHtml(nodes);
        Assert.Contains($"href=\"#{R.SectionAnchorId("arch")}\"", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"#{R.SectionAnchorId("dd-core-tour")}\"", html, StringComparison.Ordinal);
        Assert.Matches("<ul>.*dd-core.*<ul>.*dd-core-tour", html.Replace("\n", ""));
    }

    [Fact]
    public void RenderBuildsSelfContainedDocumentWithNestedTocFromDepth()
    {
        var meta = new R.ExportMeta("octo/demo", "octo", "demo", "abcdef1234567890",
            new DateTimeOffset(2026, 8, 7, 10, 0, 0, TimeSpan.Zero));
        var sections = new List<R.ExportSection>
        {
            new("arch", "Architecture", "architecture", 0, [Block("markdown", new { md = "Overview." })]),
            new("dd-core", "Deep dive: Core", "deep-dive", 1, [Block("callout", new { variant = "insight", title = "Note", md = "x" })]),
            new("dd-core-tour", "How Core works", "deep-dive-tour", 2, []),
            new("build", "Build", "build", 0, []),
        };

        var html = R.Render(meta, sections, Github);

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("<style>", html, StringComparison.Ordinal);
        Assert.Contains("exported 2026-08-07", html, StringComparison.Ordinal);
        Assert.Contains("abcdef1", html, StringComparison.Ordinal);
        Assert.Contains("<p>Overview.</p>", html, StringComparison.Ordinal);
        // Self-contained: no external stylesheet/script references anywhere in the doc.
        Assert.DoesNotMatch("<(link|script)[^>]+(href|src)=", html);
        // Nested TOC reflects section depth (tour nested under the deep dive).
        Assert.Matches("How Core works.*", html);
        Assert.Matches("<ul>.*Deep dive: Core.*<ul>.*How Core works", html.Replace("\n", ""));
    }

    [Fact]
    public void FileNameIsSanitized()
    {
        Assert.Equal("code-exploder-octo-demo.app.html", R.FileName("octo", "demo.app")); // dots kept
        Assert.Equal("code-exploder-octo-my-repo.html", R.FileName("octo", "my/repo")); // slashes → dashes
    }
}
