import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { BlockRenderer } from './BlockRenderer';
import type { Block } from '../../../api/types';

// jsdom cannot render mermaid; the dynamic import('mermaid') resolves this mock.
vi.mock('mermaid', () => ({
  default: {
    initialize: vi.fn(),
    render: vi.fn(async () => ({
      svg: '<svg><g class="node" id="flowchart-ui-1"></g><g class="node" id="flowchart-router-2"></g><g class="edgePaths"><path></path></g></svg>',
    })),
  },
}));

const github = { owner: 'vercel', repo: 'next.js', commitSha: 'abc1234def' };

function renderBlock(block: Block, firstDiagramId: string | null = null) {
  return render(
    <MemoryRouter>
      <BlockRenderer block={block} github={github} firstDiagramId={firstDiagramId} />
    </MemoryRouter>,
  );
}

afterEach(cleanup);

describe('BlockRenderer', () => {
  it('renders markdown blocks as GFM prose', () => {
    renderBlock({
      id: 'b1',
      ord: 0,
      type: 'markdown',
      data: { md: '## The request pipeline\n\nRequests enter through the **edge**.' },
    });
    expect(screen.getByRole('heading', { name: 'The request pipeline' })).toBeInTheDocument();
    expect(screen.getByText('edge')).toBeInTheDocument();
  });

  it('renders code blocks with path, line numbers from startLine, and a SHA-pinned GitHub link', () => {
    renderBlock({
      id: 'b2',
      ord: 1,
      type: 'code',
      data: {
        path: 'server/router.ts',
        startLine: 42,
        endLine: 44,
        language: 'typescript',
        content: 'export function route() {\n  return match(url);\n}\n',
        captionMd: 'The *matcher*.',
      },
    });
    expect(screen.getByText('server/router.ts')).toBeInTheDocument();
    expect(screen.getByText('42')).toBeInTheDocument();
    expect(screen.getByText('44')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'View on GitHub ↗' })).toHaveAttribute(
      'href',
      'https://github.com/vercel/next.js/blob/abc1234def/server/router.ts#L42-L44',
    );
    expect(screen.getByText('matcher')).toBeInTheDocument();
  });

  it('renders callout blocks with variant styling and title', () => {
    renderBlock({
      id: 'b3',
      ord: 2,
      type: 'callout',
      data: { variant: 'warning', title: 'Sharp edge', md: 'Mind the cache.' },
    });
    const callout = screen.getByText('Sharp edge').closest('[data-variant]');
    expect(callout).toHaveAttribute('data-variant', 'warning');
    expect(screen.getByText('Mind the cache.')).toBeInTheDocument();
  });

  it('renders diagram blocks with title, stepper, and first-stage narration', async () => {
    renderBlock(
      {
        id: 'b4',
        ord: 3,
        type: 'diagram',
        data: {
          diagramKind: 'flowchart',
          title: 'The request pipeline',
          mermaid: 'flowchart LR\n ui --> router',
          stages: [
            { title: 'Entry', narrationMd: 'Requests arrive.', reveal: { nodes: ['ui'], edges: [] } },
            { title: 'Routing', narrationMd: 'Then routing.', reveal: { nodes: ['router'], edges: [0] } },
          ],
        },
      },
      'b4',
    );
    expect(screen.getByText('The request pipeline')).toBeInTheDocument();
    expect(await screen.findByText('Stage 1 of 2')).toBeInTheDocument();
    expect(screen.getByText('Requests arrive.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous stage' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Next stage' })).toBeEnabled();
  });

  it('renders timeline diagrams fully visible with a narration-walking stepper (no ghosting, no warning)', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const { container } = renderBlock({
      id: 'b5',
      ord: 4,
      type: 'diagram',
      data: {
        diagramKind: 'timeline',
        title: 'The origin story',
        mermaid: 'timeline\n 2019 : first commit\n 2021 : v1.0',
        stages: [
          { title: 'The spark', narrationMd: 'One developer, one weekend.', reveal: { nodes: [], edges: [] } },
          { title: 'Going public', narrationMd: 'v1.0 ships.', reveal: { nodes: [], edges: [] } },
        ],
      },
    });
    expect(await screen.findByText('Stage 1 of 2')).toBeInTheDocument();
    expect(screen.getByText('One developer, one weekend.')).toBeInTheDocument();
    // Fully visible: the mock SVG's nodes are never ghosted for timelines.
    expect(container.querySelectorAll('.ghost')).toHaveLength(0);
    fireEvent.click(screen.getByRole('button', { name: 'Next stage' }));
    expect(await screen.findByText('Stage 2 of 2')).toBeInTheDocument();
    expect(screen.getByText('v1.0 ships.')).toBeInTheDocument();
    expect(container.querySelectorAll('.ghost')).toHaveLength(0);
    expect(warnSpy).not.toHaveBeenCalled();
    warnSpy.mockRestore();
  });
});
