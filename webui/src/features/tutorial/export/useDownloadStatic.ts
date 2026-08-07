import { useCallback, useState } from 'react';
import { api, ApiError } from '../../../api/client';
import type { Block, ExperienceToc, SessionSummary } from '../../../api/types';
import { useUi } from '../../../store/ui';
import { buildTocTree, flattenAll } from '../tocTree';
import {
  buildDocument,
  calloutHtml,
  codeBlockHtml,
  diagramHtml,
  sectionHtml,
  tocHtml,
  type GithubExportContext,
  type RenderedSection,
} from './staticExport';

/** Bounded-concurrency map (export is not latency-critical, but many sections shouldn't storm the API). */
async function pool<T, R>(items: T[], limit: number, fn: (item: T) => Promise<R>): Promise<R[]> {
  const results = new Array<R>(items.length);
  let next = 0;
  const worker = async () => {
    while (next < items.length) {
      const i = next++;
      results[i] = await fn(items[i]);
    }
  };
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}

/** react-dom/server is heavy and only needed on export — load it (and the markdown pipeline) lazily. */
async function loadMarkdownRenderer(): Promise<(md: string) => string> {
  const [server, react, reactMarkdown, remarkGfm] = await Promise.all([
    import('react-dom/server'),
    import('react'),
    import('react-markdown'),
    import('remark-gfm'),
  ]);
  return (md: string) =>
    server.renderToStaticMarkup(
      react.createElement(reactMarkdown.default, { remarkPlugins: [remarkGfm.default] }, md),
    );
}

async function loadDiagramRenderer(): Promise<(id: string, src: string) => Promise<string | null>> {
  const mermaid = (await import('mermaid')).default;
  // 'default' theme reads on both light and dark pages (its SVGs carry their own
  // light backgrounds); 'strict' sanitizes so the inlined SVG is safe.
  mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'default' });
  return async (id, src) => {
    try {
      const { svg } = await mermaid.render(id, src);
      return svg;
    } catch {
      return null;
    }
  };
}

function blockHtml(
  block: Block,
  github: GithubExportContext,
  renderMd: (md: string) => string,
): string {
  switch (block.type) {
    case 'markdown':
      return `<div class="prose">${renderMd(block.data.md)}</div>`;
    case 'code':
      return codeBlockHtml(block.data, github, block.data.captionMd ? renderMd(block.data.captionMd) : undefined);
    case 'callout':
      return calloutHtml(block.data, renderMd(block.data.md));
    default:
      return ''; // diagrams handled separately (async mermaid render)
  }
}

function sanitizeFilename(s: string): string {
  return s.replace(/[^a-zA-Z0-9._-]+/g, '-').replace(/^-+|-+$/g, '') || 'export';
}

/**
 * Builds a single self-contained HTML file of the reading tour (all ready
 * sections in tree order, deep dives and story included) and downloads it.
 * Quizzes and the Q&A chat are interactive and intentionally omitted.
 */
export function useDownloadStatic(toc: ExperienceToc, session: SessionSummary) {
  const [isExporting, setExporting] = useState(false);
  const toast = useUi((s) => s.toast);

  const download = useCallback(async () => {
    if (isExporting) return;
    setExporting(true);
    try {
      const ready = toc.sections.filter((s) => s.status === 'ready');
      if (ready.length === 0) {
        toast('info', 'Nothing to export yet', 'No sections are ready.');
        return;
      }

      const tree = buildTocTree(ready);
      const ordered = flattenAll(tree);
      const [renderMd, renderDiagram] = await Promise.all([
        loadMarkdownRenderer(),
        loadDiagramRenderer(),
      ]);

      const details = await pool(ordered, 6, (entry) => api.getSection(entry.id));
      const github: GithubExportContext = {
        owner: session.repoOwner,
        repo: session.repoName,
        commitSha: toc.commitSha,
      };

      const rendered: RenderedSection[] = [];
      let diagramSeq = 0;
      for (let s = 0; s < ordered.length; s++) {
        const entry = ordered[s];
        const detail = details[s];
        const blocks = [...detail.blocks].sort((a, b) => a.ord - b.ord);
        const blocksHtml: string[] = [];
        for (const block of blocks) {
          if (block.type === 'diagram') {
            // Sequential: mermaid.render shares a sandbox and can't run concurrently.
            const svg = await renderDiagram(`export-diagram-${diagramSeq++}`, block.data.mermaid);
            const narration = block.data.stages.map((st) => renderMd(st.narrationMd));
            blocksHtml.push(diagramHtml(block.data, svg, narration));
          } else {
            blocksHtml.push(blockHtml(block, github, renderMd));
          }
        }
        rendered.push({
          slug: entry.slug,
          title: entry.title,
          kind: entry.kind,
          depth: entry.depth,
          blocksHtml,
        });
      }

      const html = buildDocument(
        {
          repoTitle: session.title,
          repoOwner: session.repoOwner,
          repoName: session.repoName,
          commitSha: toc.commitSha,
          generatedAtIso: new Date().toISOString(),
          sectionCount: rendered.length,
        },
        tocHtml(tree),
        rendered.map(sectionHtml).join('\n'),
      );

      const blob = new Blob([html], { type: 'text/html;charset=utf-8' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `code-exploder-${sanitizeFilename(`${session.repoOwner}-${session.repoName}`)}.html`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      URL.revokeObjectURL(url);
      toast('success', 'Offline copy downloaded', `${rendered.length} sections`);
    } catch (err) {
      toast(
        'error',
        'Export failed',
        err instanceof ApiError ? err.message : err instanceof Error ? err.message : 'Unexpected error',
      );
    } finally {
      setExporting(false);
    }
  }, [isExporting, toc, session, toast]);

  return { download, isExporting };
}
