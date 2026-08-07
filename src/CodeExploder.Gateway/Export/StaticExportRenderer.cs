using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Markdig;

namespace CodeExploder.Gateway.Export;

/// <summary>
/// Server-side twin of the client offline export (webui staticExport.ts): renders an
/// exploded session to a single self-contained HTML document for `GET
/// …/export.html`. Same CSS/structure as the in-app download, so they read alike.
///
/// One deliberate difference: the gateway has no JS runtime, so it cannot render
/// mermaid to SVG the way the browser export does. To keep this file lightweight and
/// truly no-JS/offline, diagrams appear as their stage-narration walkthrough plus the
/// raw Mermaid source in a collapsible block — never a multi-MB inlined renderer. Use
/// the in-app Download button when you want rendered diagram SVGs.
/// </summary>
public static class StaticExportRenderer
{
    public sealed record GithubContext(string Owner, string Repo, string CommitSha);

    public sealed record ExportBlock(string Type, string DataJson);

    public sealed record ExportSection(
        string Slug, string Title, string Kind, int Depth, IReadOnlyList<ExportBlock> Blocks);

    public sealed record ExportMeta(
        string RepoTitle, string RepoOwner, string RepoName, string CommitSha, DateTimeOffset GeneratedAt);

    private static readonly MarkdownPipeline MdPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
        .DisableHtml() // never emit raw HTML from LLM-authored markdown (matches react-markdown)
        .Build();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static string FileName(string owner, string name) =>
        $"code-exploder-{Slug($"{owner}-{name}")}.html";

    /// <summary>Renders the whole document. Sections must already be in reading (tree) order.</summary>
    public static string Render(ExportMeta meta, IReadOnlyList<ExportSection> sections, GithubContext github)
    {
        var toc = TocHtml(BuildForest(sections));
        var body = new StringBuilder();
        foreach (var section in sections)
        {
            var blocks = section.Blocks.Select(b => BlockHtml(b, github)).ToList();
            body.Append(SectionHtml(section.Slug, section.Title, section.Kind, section.Depth, blocks)).Append('\n');
        }

        return BuildDocument(meta, sections.Count, toc, body.ToString());
    }

    // --- pure builders (mirror webui staticExport.ts) ---

    public static string EscapeHtml(string s) => s
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    public static string SectionAnchorId(string slug) =>
        "sec-" + new string(slug.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-').ToArray());

    public static string RenderMarkdown(string? md) => Markdown.ToHtml(md ?? string.Empty, MdPipeline);

    public static string BlockHtml(ExportBlock block, GithubContext github)
    {
        switch (block.Type)
        {
            case "markdown":
                return $"<div class=\"prose\">{RenderMarkdown(Parse<MarkdownDto>(block).Md)}</div>";
            case "code":
                return CodeBlockHtml(Parse<CodeDto>(block), github);
            case "callout":
            {
                var c = Parse<CalloutDto>(block);
                return CalloutHtml(c, RenderMarkdown(c.Md));
            }

            case "diagram":
            {
                var d = Parse<DiagramDto>(block);
                var narration = (d.Stages ?? []).Select(s => RenderMarkdown(s.NarrationMd)).ToList();
                return DiagramHtml(d, narration);
            }

            default:
                return string.Empty;
        }
    }

    public static string CodeBlockHtml(CodeDto data, GithubContext? github)
    {
        var isDiff = string.Equals(data.Language, "Diff", StringComparison.Ordinal);
        var lines = (data.Content ?? string.Empty).TrimEnd('\n').Split('\n');
        var rows = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var cls = isDiff ? "line " + DiffClass(lines[i]) : "line";
            var escaped = EscapeHtml(lines[i]);
            rows.Append($"<div class=\"{cls}\"><span class=\"ln\" aria-hidden=\"true\">{data.StartLine + i}</span>")
                .Append($"<span class=\"code\">{(escaped.Length == 0 ? "&nbsp;" : escaped)}</span></div>");
        }

        var link = github is null
            ? string.Empty
            : $"<a class=\"gh\" href=\"{EscapeHtml($"https://github.com/{github.Owner}/{github.Repo}/blob/{github.CommitSha}/{data.Path}#L{data.StartLine}-L{data.EndLine}")}\" target=\"_blank\" rel=\"noreferrer\">View on GitHub ↗</a>";
        var caption = string.IsNullOrEmpty(data.CaptionMd)
            ? string.Empty
            : $"<figcaption>{RenderMarkdown(data.CaptionMd)}</figcaption>";
        return $"<figure class=\"code\"><div class=\"code-head\"><span class=\"path\">{EscapeHtml(data.Path)}</span>{link}</div>" +
               $"<div class=\"code-body\" data-language=\"{EscapeHtml(data.Language)}\">{rows}</div>{caption}</figure>";
    }

    public static string CalloutHtml(CalloutDto data, string bodyHtml)
    {
        var (color, glyph) = data.Variant switch
        {
            "warning" => ("var(--x-warning)", "▲"),
            "convention" => ("var(--x-keep)", "§"),
            _ => ("var(--x-accent)", "◆"),
        };
        return $"<aside class=\"callout\" style=\"border-left-color:{color}\">" +
               $"<div class=\"callout-title\"><span aria-hidden=\"true\" style=\"color:{color}\">{glyph}</span> {EscapeHtml(data.Title)}</div>" +
               $"{bodyHtml}</aside>";
    }

    public static string DiagramHtml(DiagramDto data, IReadOnlyList<string> narrationHtml)
    {
        var stages = data.Stages ?? [];
        var steps = stages.Count == 0
            ? string.Empty
            : "<ol class=\"diagram-steps\">" +
              string.Concat(stages.Select((s, i) =>
                  $"<li><span class=\"step-title\">{EscapeHtml(s.Title)}</span>{(i < narrationHtml.Count ? narrationHtml[i] : string.Empty)}</li>")) +
              "</ol>";
        var title = string.IsNullOrEmpty(data.Title)
            ? string.Empty
            : $"<figcaption class=\"diagram-title\">{EscapeHtml(data.Title)}</figcaption>";
        var source = string.IsNullOrWhiteSpace(data.Mermaid)
            ? string.Empty
            : $"<details class=\"diagram-source\"><summary>Diagram source (Mermaid)</summary><pre>{EscapeHtml(data.Mermaid)}</pre></details>";
        return $"<figure class=\"diagram\">{title}{steps}{source}</figure>";
    }

    public static string SectionHtml(string slug, string title, string kind, int depth, IReadOnlyList<string> blocksHtml)
    {
        var level = Math.Min(2 + depth, 6);
        var id = SectionAnchorId(slug);
        return $"<section id=\"{id}\" class=\"section depth-{depth}\">" +
               $"<h{level} class=\"section-title\">{EscapeHtml(title)}<a class=\"anchor\" href=\"#{id}\" aria-label=\"Link to section\">#</a></h{level}>" +
               $"<p class=\"section-kind\">{EscapeHtml(kind)}</p>" +
               string.Join("\n", blocksHtml) +
               "</section>";
    }

    public static string TocHtml(IReadOnlyList<TocNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return string.Empty;
        }

        var items = string.Concat(nodes.Select(n =>
            $"<li><a href=\"#{SectionAnchorId(n.Slug)}\">{EscapeHtml(n.Title)}</a>{TocHtml(n.Children)}</li>"));
        return $"<ul>{items}</ul>";
    }

    public static string BuildDocument(ExportMeta meta, int sectionCount, string tocMarkup, string sectionsMarkup)
    {
        var sha7 = meta.CommitSha.Length >= 7 ? meta.CommitSha[..7] : meta.CommitSha;
        var date = meta.GeneratedAt.ToString("yyyy-MM-dd");
        var repo = $"{meta.RepoOwner}/{meta.RepoName}";
        return "<!doctype html>\n<html lang=\"en\">\n<head>\n" +
               "<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n" +
               $"<title>{EscapeHtml(meta.RepoTitle)} — Code Exploder</title>\n" +
               $"<style>{ExportCss}</style>\n</head>\n<body>\n" +
               $"<header class=\"doc-head\"><h1>{EscapeHtml(meta.RepoTitle)}</h1>" +
               $"<p class=\"sub\">A Code Exploder tour of <strong>{EscapeHtml(repo)}</strong> · commit <code>{EscapeHtml(sha7)}</code> · {sectionCount} sections · exported {EscapeHtml(date)}</p></header>\n" +
               $"<nav class=\"toc\" aria-label=\"Contents\"><h2>Contents</h2>{tocMarkup}</nav>\n" +
               $"<main>\n{sectionsMarkup}\n</main>\n" +
               "<footer class=\"doc-foot\">Generated offline by Code Exploder. Quizzes and the interactive Q&amp;A are omitted from this static copy; diagrams show their source and step-by-step narration (the in-app Download renders them as images). “View on GitHub” links need a connection.</footer>\n" +
               "</body>\n</html>\n";
    }

    // --- tree order (mirror webui tocTree.buildTocTree/flattenAll) ---

    public sealed record TocNode(string Slug, string Title, List<TocNode> Children);

    private static List<TocNode> BuildForest(IReadOnlyList<ExportSection> ordered)
    {
        // `ordered` is already the depth-first tree order; reconstruct nesting by depth.
        var roots = new List<TocNode>();
        var stack = new List<TocNode>();
        foreach (var s in ordered)
        {
            var node = new TocNode(s.Slug, s.Title, []);
            while (stack.Count > s.Depth)
            {
                stack.RemoveAt(stack.Count - 1);
            }

            if (stack.Count == 0)
            {
                roots.Add(node);
            }
            else
            {
                stack[^1].Children.Add(node);
            }

            stack.Add(node);
        }

        return roots;
    }

    private static string DiffClass(string line) =>
        line.StartsWith("@@", StringComparison.Ordinal) ? "hunk"
        : line.StartsWith('+') ? "added"
        : line.StartsWith('-') ? "removed"
        : "";

    private static T Parse<T>(ExportBlock block) =>
        JsonSerializer.Deserialize<T>(block.DataJson, JsonOpts)
        ?? throw new JsonException($"empty {block.Type} block data");

    // Block data DTOs (the stored jsonb shapes, docs/03 + Pipeline/Generators.cs).
    public sealed record MarkdownDto([property: JsonPropertyName("md")] string Md);

    public sealed record CodeDto(string Path, int StartLine, int EndLine, string Language, string Content, string? CaptionMd);

    public sealed record CalloutDto(string Variant, string Title, string Md);

    public sealed record DiagramDto(string DiagramKind, string Title, string Mermaid, List<DiagramStageDto>? Stages);

    public sealed record DiagramStageDto(string Title, string NarrationMd);

    // Kept in sync with webui/src/features/tutorial/export/staticExport.ts EXPORT_CSS,
    // with an added .diagram-source rule for the no-JS diagram fallback.
    private const string ExportCss = """
:root{--x-bg:#fff;--x-fg:#1a1a1a;--x-muted:#5c6470;--x-border:#e2e4e8;--x-code-bg:#f6f7f9;
--x-accent:#3b5bdb;--x-warning:#b8860b;--x-keep:#2b8a3e;--x-added:#e6f4ea;--x-removed:#fce8e8;--x-hunk:#eef1f6;}
@media (prefers-color-scheme:dark){:root{--x-bg:#15171b;--x-fg:#e6e8ec;--x-muted:#9aa2ad;--x-border:#2a2e35;
--x-code-bg:#1c1f25;--x-accent:#7aa2ff;--x-warning:#e0b341;--x-keep:#6bd08a;--x-added:#16311f;--x-removed:#3a1d1f;--x-hunk:#20242c;}}
*{box-sizing:border-box}
html{-webkit-text-size-adjust:100%}
body{margin:0;background:var(--x-bg);color:var(--x-fg);
font:16px/1.65 ui-sans-serif,-apple-system,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;}
.doc-head,.toc,main,.doc-foot{max-width:820px;margin:0 auto;padding:0 24px}
.doc-head{padding-top:48px;border-bottom:1px solid var(--x-border);padding-bottom:20px}
.doc-head h1{font-size:2rem;margin:0 0 6px}
.doc-head .sub{color:var(--x-muted);font-size:.9rem;margin:0}
.toc{margin-top:28px}
.toc h2{font-size:.8rem;text-transform:uppercase;letter-spacing:.08em;color:var(--x-muted)}
.toc ul{list-style:none;padding-left:16px;margin:.3em 0}
.toc>ul{padding-left:0}
.toc a{color:var(--x-accent);text-decoration:none}
.toc a:hover{text-decoration:underline}
main{margin-top:16px;padding-bottom:64px}
.section{padding-top:24px;margin-top:24px;border-top:1px solid var(--x-border)}
.section.depth-0{border-top-width:2px}
.section-title{scroll-margin-top:16px;line-height:1.25}
.section-title .anchor{margin-left:.4em;color:var(--x-border);text-decoration:none;font-weight:400}
.section-title:hover .anchor{color:var(--x-muted)}
.section-kind{margin:-.4em 0 1em;font-size:.7rem;text-transform:uppercase;letter-spacing:.08em;color:var(--x-muted)}
h2.section-title{font-size:1.6rem}h3.section-title{font-size:1.3rem}h4.section-title{font-size:1.12rem}
h5.section-title,h6.section-title{font-size:1rem}
p{margin:0 0 1em}
a{color:var(--x-accent)}
code{font:.88em ui-monospace,SFMono-Regular,"JetBrains Mono",Menlo,Consolas,monospace;
background:var(--x-code-bg);padding:.1em .35em;border-radius:4px}
pre code,.code code{background:none;padding:0}
img,svg{max-width:100%}
ul,ol{padding-left:1.4em}
blockquote{margin:1em 0;padding-left:1em;border-left:3px solid var(--x-border);color:var(--x-muted)}
table{border-collapse:collapse;width:100%;margin:1em 0;font-size:.92em;display:block;overflow-x:auto}
th,td{border:1px solid var(--x-border);padding:6px 10px;text-align:left}
.callout{margin:1.25em 0;padding:12px 16px;border:1px solid var(--x-border);border-left-width:4px;
border-radius:6px;background:var(--x-code-bg)}
.callout-title{font-weight:600;margin-bottom:.3em}
.callout p:last-child{margin-bottom:0}
figure{margin:1.25em 0}
.code{border:1px solid var(--x-border);border-radius:8px;overflow:hidden}
.code-head{display:flex;justify-content:space-between;gap:12px;align-items:center;
padding:6px 12px;background:var(--x-code-bg);border-bottom:1px solid var(--x-border);font-size:.8rem}
.code-head .path{font-family:ui-monospace,monospace;color:var(--x-muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.code-head .gh{white-space:nowrap;text-decoration:none}
.code-body{overflow-x:auto;padding:8px 0;
font:.82rem/1.55 ui-monospace,SFMono-Regular,"JetBrains Mono",Menlo,Consolas,monospace}
.code-body .line{display:flex;white-space:pre;padding:0 12px}
.code-body .ln{display:inline-block;min-width:3ch;margin-right:16px;text-align:right;color:var(--x-muted);user-select:none}
.code-body .added{background:var(--x-added)}
.code-body .removed{background:var(--x-removed)}
.code-body .hunk{background:var(--x-hunk);color:var(--x-muted)}
.diagram{text-align:center}
.diagram-title{margin-top:8px;font-size:.8rem;color:var(--x-muted)}
.diagram-steps{text-align:left;margin-top:12px;padding-left:1.4em}
.diagram-steps .step-title{font-weight:600;margin-right:.4em}
.diagram-source{text-align:left;margin-top:10px;font-size:.85rem;color:var(--x-muted)}
.diagram-source summary{cursor:pointer}
.diagram-source pre{overflow-x:auto;background:var(--x-code-bg);border:1px solid var(--x-border);
border-radius:6px;padding:10px;font:.8rem ui-monospace,SFMono-Regular,Menlo,Consolas,monospace}
.doc-foot{margin-top:32px;padding:20px 24px 48px;border-top:1px solid var(--x-border);
color:var(--x-muted);font-size:.82rem}
@media print{.toc{page-break-after:always}.section{page-break-inside:avoid}a{color:inherit}}
""";

    private static string Slug(string s)
    {
        var chars = s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray();
        var collapsed = new string(chars).Trim('-');
        return collapsed.Length == 0 ? "export" : collapsed;
    }
}
