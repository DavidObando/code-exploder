import type { CalloutData, CodeBlockData, DiagramData } from '../../../api/types';
import { classifyDiffLine, type DiffLineKind } from '../blocks/CodeExcerpt';
import type { TocNode } from '../tocTree';

// Pure builders for the offline static export: a single self-contained HTML
// document of the reading tour. Markdown and mermaid are rendered upstream (by
// useDownloadStatic) and passed in as HTML/SVG strings, so everything here is
// pure and unit-testable — no DOM, no async, no React.

export interface GithubExportContext {
  owner: string;
  repo: string;
  commitSha: string;
}

/** One section's already-rendered blocks, in order. */
export interface RenderedSection {
  slug: string;
  title: string;
  kind: string;
  depth: number;
  blocksHtml: string[];
}

export function escapeHtml(s: string): string {
  return s
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

/** Slug → DOM id for in-document TOC anchors. */
export function sectionAnchorId(slug: string): string {
  return 'sec-' + slug.replace(/[^a-zA-Z0-9_-]/g, '-');
}

const diffLineClass: Record<DiffLineKind, string> = {
  added: 'line added',
  removed: 'line removed',
  hunk: 'line hunk',
  context: 'line',
};

/**
 * Code excerpt as static HTML: a header (path + SHA-pinned GitHub link) and a
 * line-numbered block. Mirrors CodeExcerpt: numbers count from startLine, and
 * a 'Diff' language tints add/remove/hunk lines.
 */
export function codeBlockHtml(
  data: CodeBlockData,
  github: GithubExportContext | null,
  captionHtml?: string,
): string {
  const isDiff = data.language === 'Diff';
  const lines = data.content.replace(/\n$/, '').split('\n');
  const rows = lines
    .map((line, i) => {
      const cls = isDiff ? diffLineClass[classifyDiffLine(line)] : 'line';
      return (
        `<div class="${cls}">` +
        `<span class="ln" aria-hidden="true">${data.startLine + i}</span>` +
        `<span class="code">${escapeHtml(line) || '&nbsp;'}</span>` +
        `</div>`
      );
    })
    .join('');
  const href = github
    ? `https://github.com/${github.owner}/${github.repo}/blob/${github.commitSha}/${data.path}#L${data.startLine}-L${data.endLine}`
    : null;
  const link = href
    ? `<a class="gh" href="${escapeHtml(href)}" target="_blank" rel="noreferrer">View on GitHub ↗</a>`
    : '';
  const caption = captionHtml ? `<figcaption>${captionHtml}</figcaption>` : '';
  return (
    `<figure class="code">` +
    `<div class="code-head"><span class="path">${escapeHtml(data.path)}</span>${link}</div>` +
    `<div class="code-body" data-language="${escapeHtml(data.language)}">${rows}</div>` +
    `${caption}</figure>`
  );
}

const calloutAccent: Record<CalloutData['variant'], { color: string; glyph: string }> = {
  insight: { color: 'var(--x-accent)', glyph: '◆' },
  warning: { color: 'var(--x-warning)', glyph: '▲' },
  convention: { color: 'var(--x-keep)', glyph: '§' },
};

/** Left-accent callout card. bodyHtml is the rendered markdown body. */
export function calloutHtml(data: CalloutData, bodyHtml: string): string {
  const { color, glyph } = calloutAccent[data.variant] ?? calloutAccent.insight;
  return (
    `<aside class="callout" style="border-left-color:${color}">` +
    `<div class="callout-title"><span aria-hidden="true" style="color:${color}">${glyph}</span> ${escapeHtml(data.title)}</div>` +
    `${bodyHtml}</aside>`
  );
}

/**
 * Diagram as static HTML: the fully-revealed SVG plus the stage narrations as a
 * numbered walkthrough (progressive reveal is interactive; the offline copy
 * shows the whole diagram and lists the steps). On a render failure, svg is null
 * and only the narration walkthrough is shown so no content is lost.
 */
export function diagramHtml(data: DiagramData, svg: string | null, narrationHtml: string[]): string {
  const figure = svg
    ? `<div class="diagram-svg">${svg}</div>`
    : `<div class="diagram-fallback">(diagram “${escapeHtml(data.title)}” could not be rendered — walkthrough below)</div>`;
  const steps = narrationHtml.length
    ? `<ol class="diagram-steps">` +
      data.stages
        .map((stage, i) => `<li><span class="step-title">${escapeHtml(stage.title)}</span>${narrationHtml[i] ?? ''}</li>`)
        .join('') +
      `</ol>`
    : '';
  const title = data.title ? `<figcaption class="diagram-title">${escapeHtml(data.title)}</figcaption>` : '';
  return `<figure class="diagram">${figure}${title}${steps}</figure>`;
}

/** One section: depth-derived heading + anchor + its blocks. */
export function sectionHtml(section: RenderedSection): string {
  const level = Math.min(2 + section.depth, 6); // h1 is the doc title
  const id = sectionAnchorId(section.slug);
  return (
    `<section id="${id}" class="section depth-${section.depth}">` +
    `<h${level} class="section-title">${escapeHtml(section.title)}` +
    `<a class="anchor" href="#${id}" aria-label="Link to section">#</a></h${level}>` +
    `<p class="section-kind">${escapeHtml(section.kind)}</p>` +
    section.blocksHtml.join('\n') +
    `</section>`
  );
}

/** Nested table of contents from the section tree (ready sections only). */
export function tocHtml(nodes: TocNode[]): string {
  if (nodes.length === 0) return '';
  const items = nodes
    .map(
      (n) =>
        `<li><a href="#${sectionAnchorId(n.entry.slug)}">${escapeHtml(n.entry.title)}</a>${tocHtml(n.children)}</li>`,
    )
    .join('');
  return `<ul>${items}</ul>`;
}

export interface DocumentMeta {
  repoTitle: string;
  repoOwner: string;
  repoName: string;
  commitSha: string;
  generatedAtIso: string;
  sectionCount: number;
}

/** Assembles the complete self-contained HTML document. */
export function buildDocument(meta: DocumentMeta, tocMarkup: string, sectionsMarkup: string): string {
  const sha7 = meta.commitSha.slice(0, 7);
  const date = meta.generatedAtIso.slice(0, 10);
  const repo = `${meta.repoOwner}/${meta.repoName}`;
  return (
    `<!doctype html>\n` +
    `<html lang="en">\n<head>\n` +
    `<meta charset="utf-8">\n` +
    `<meta name="viewport" content="width=device-width, initial-scale=1">\n` +
    `<title>${escapeHtml(meta.repoTitle)} — Code Exploder</title>\n` +
    `<style>${EXPORT_CSS}</style>\n` +
    `</head>\n<body>\n` +
    `<header class="doc-head">` +
    `<h1>${escapeHtml(meta.repoTitle)}</h1>` +
    `<p class="sub">A Code Exploder tour of <strong>${escapeHtml(repo)}</strong> · commit <code>${escapeHtml(sha7)}</code> · ${meta.sectionCount} sections · exported ${escapeHtml(date)}</p>` +
    `</header>\n` +
    `<nav class="toc" aria-label="Contents"><h2>Contents</h2>${tocMarkup}</nav>\n` +
    `<main>\n${sectionsMarkup}\n</main>\n` +
    `<footer class="doc-foot">Generated offline by Code Exploder. Quizzes and the interactive Q&amp;A are omitted from this static copy; “View on GitHub” links need a connection.</footer>\n` +
    `</body>\n</html>\n`
  );
}

// Self-contained stylesheet: system fonts, a readable single column, light/dark
// aware, and print-friendly. No external requests so the file opens offline.
export const EXPORT_CSS = `
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
.diagram-svg{overflow-x:auto}
.diagram-title{margin-top:8px;font-size:.8rem;color:var(--x-muted)}
.diagram-fallback{color:var(--x-muted);font-style:italic;padding:12px}
.diagram-steps{text-align:left;margin-top:12px;padding-left:1.4em}
.diagram-steps .step-title{font-weight:600;margin-right:.4em}
.doc-foot{margin-top:32px;padding:20px 24px 48px;border-top:1px solid var(--x-border);
color:var(--x-muted);font-size:.82rem}
@media print{.toc{page-break-after:always}.section{page-break-inside:avoid}a{color:inherit}}
`;
