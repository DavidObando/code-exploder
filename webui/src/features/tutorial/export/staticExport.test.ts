import type { CalloutData, CodeBlockData, DiagramData, SectionTocEntry } from '../../../api/types';
import { buildTocTree } from '../tocTree';
import {
  buildDocument,
  calloutHtml,
  codeBlockHtml,
  diagramHtml,
  escapeHtml,
  sectionAnchorId,
  sectionHtml,
  tocHtml,
  type GithubExportContext,
} from './staticExport';

const github: GithubExportContext = { owner: 'octo', repo: 'demo', commitSha: 'abcdef1234567890' };

function entry(o: Partial<SectionTocEntry>): SectionTocEntry {
  return {
    id: 'id', slug: 'slug', kind: 'architecture', title: 'T', summary: '', ord: 0, depth: 0,
    parentSectionId: null, estimatedMinutes: 5, status: 'ready', myState: 'unread',
    hasQuiz: false, quizBestPct: null, componentId: null, ...o,
  };
}

describe('escapeHtml', () => {
  it('escapes all HTML-significant characters', () => {
    expect(escapeHtml(`<script>"a"&'b'`)).toBe('&lt;script&gt;&quot;a&quot;&amp;&#39;b&#39;');
  });
});

describe('codeBlockHtml', () => {
  const data: CodeBlockData = {
    path: 'src/Binder.cs', startLine: 17, endLine: 19, language: 'C#',
    content: 'namespace X;\nclass Binder\n{', captionMd: null,
  };

  it('numbers lines from startLine and escapes content', () => {
    const html = codeBlockHtml({ ...data, content: 'var x = a < b && "y";' }, github);
    expect(html).toContain('>17<'); // first line number
    expect(html).toContain('a &lt; b &amp;&amp; &quot;y&quot;');
    expect(html).not.toContain('<b'); // never raw
  });

  it('numbers each subsequent line', () => {
    const html = codeBlockHtml(data, github);
    expect(html).toContain('>17<');
    expect(html).toContain('>18<');
    expect(html).toContain('>19<');
  });

  it('builds a SHA-pinned GitHub deep link', () => {
    const html = codeBlockHtml(data, github);
    expect(html).toContain('https://github.com/octo/demo/blob/abcdef1234567890/src/Binder.cs#L17-L19');
  });

  it('omits the link when github context is null', () => {
    expect(codeBlockHtml(data, null)).not.toContain('View on GitHub');
  });

  it('tints diff lines by kind', () => {
    const diff: CodeBlockData = {
      path: 'a.ts', startLine: 1, endLine: 3, language: 'Diff',
      content: '@@ -1 +1 @@\n-old\n+new', captionMd: null,
    };
    const html = codeBlockHtml(diff, github);
    expect(html).toContain('class="line hunk"');
    expect(html).toContain('class="line removed"');
    expect(html).toContain('class="line added"');
  });
});

describe('calloutHtml', () => {
  it('renders title, variant accent, and the pre-rendered body', () => {
    const data: CalloutData = { variant: 'warning', title: 'Careful <x>', md: 'ignored' };
    const html = calloutHtml(data, '<p>body</p>');
    expect(html).toContain('Careful &lt;x&gt;');
    expect(html).toContain('var(--x-warning)');
    expect(html).toContain('<p>body</p>');
  });
});

describe('diagramHtml', () => {
  const data: DiagramData = {
    diagramKind: 'flowchart', title: 'Flow', mermaid: 'flowchart TD',
    stages: [
      { title: 'One', narrationMd: '', reveal: { nodes: [], edges: [] } },
      { title: 'Two', narrationMd: '', reveal: { nodes: [], edges: [] } },
    ],
  };

  it('inlines the SVG and lists stage narrations', () => {
    const html = diagramHtml(data, '<svg id="d"></svg>', ['<p>first</p>', '<p>second</p>']);
    expect(html).toContain('<svg id="d"></svg>');
    expect(html).toContain('One');
    expect(html).toContain('<p>first</p>');
    expect(html).toContain('Two');
  });

  it('falls back to the walkthrough when the SVG failed to render', () => {
    const html = diagramHtml(data, null, ['<p>first</p>', '<p>second</p>']);
    expect(html).toContain('could not be rendered');
    expect(html).toContain('<p>first</p>');
    expect(html).not.toContain('<svg');
  });
});

describe('sectionHtml', () => {
  it('picks a heading level from depth and anchors on the slug', () => {
    const h2 = sectionHtml({ slug: 'intro', title: 'Intro', kind: 'intro', depth: 0, blocksHtml: ['<p>a</p>'] });
    expect(h2).toContain('<h2');
    expect(h2).toContain(`id="${sectionAnchorId('intro')}"`);
    expect(h2).toContain('<p>a</p>');

    const h4 = sectionHtml({ slug: 'dd-x-tour', title: 'Deep', kind: 'deep-dive-tour', depth: 2, blocksHtml: [] });
    expect(h4).toContain('<h4');

    const capped = sectionHtml({ slug: 's', title: 'Deep', kind: 'x', depth: 9, blocksHtml: [] });
    expect(capped).toContain('<h6');
  });
});

describe('tocHtml', () => {
  it('nests the tree and links each entry to its anchor', () => {
    const sections = [
      entry({ id: 'a', slug: 'arch', title: 'Arch', ord: 1 }),
      entry({ id: 'dd', slug: 'dd-core', kind: 'deep-dive', ord: 2, depth: 1, parentSectionId: 'a' }),
      entry({ id: 't', slug: 'dd-core-tour', ord: 3, depth: 2, parentSectionId: 'dd' }),
    ];
    const html = tocHtml(buildTocTree(sections));
    expect(html).toContain(`href="#${sectionAnchorId('arch')}"`);
    expect(html).toContain(`href="#${sectionAnchorId('dd-core-tour')}"`);
    // nesting: the tour link sits inside a nested <ul>
    expect(html).toMatch(/<ul>.*dd-core.*<ul>.*dd-core-tour/s);
  });

  it('is empty for no sections', () => {
    expect(tocHtml([])).toBe('');
  });
});

describe('buildDocument', () => {
  it('produces one self-contained HTML doc with inlined styles and no external requests', () => {
    const html = buildDocument(
      {
        repoTitle: 'octo/demo', repoOwner: 'octo', repoName: 'demo',
        commitSha: 'abcdef1234567890', generatedAtIso: '2026-08-07T10:00:00.000Z', sectionCount: 3,
      },
      '<ul><li>x</li></ul>',
      '<section>content</section>',
    );
    expect(html.startsWith('<!doctype html>')).toBe(true);
    expect(html).toContain('<style>');
    expect(html).toContain('abcdef1'); // short sha
    expect(html).toContain('exported 2026-08-07');
    expect(html).toContain('<section>content</section>');
    // Self-contained: no http(s) asset references in the shell (GitHub deep links
    // live inside section content, not the document chrome).
    expect(html).not.toMatch(/<(link|script)[^>]+(href|src)=/);
  });
});
