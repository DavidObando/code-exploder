import { render, screen, cleanup } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SectionNav, sectionGlyph } from './SectionNav';
import type { ExperienceToc, SectionTocEntry } from '../../api/types';

function entry(overrides: Partial<SectionTocEntry>): SectionTocEntry {
  return {
    id: 'id',
    slug: 'slug',
    kind: 'architecture',
    title: 'Untitled',
    summary: '',
    ord: 0,
    depth: 0,
    parentSectionId: null,
    estimatedMinutes: 5,
    status: 'ready',
    myState: 'unread',
    hasQuiz: false,
    quizBestPct: null,
    ...overrides,
  };
}

const toc: ExperienceToc = {
  experienceId: 'exp1',
  version: 1,
  commitSha: 'abc123',
  model: 'llama',
  generatedAt: '2026-07-30T00:00:00Z',
  sections: [
    entry({
      id: 's1',
      slug: 'intro',
      title: 'Intro',
      ord: 0,
      myState: 'completed',
      hasQuiz: true,
      quizBestPct: 80,
    }),
    entry({ id: 's2', slug: 'arch', title: 'Architecture', ord: 1, myState: 'read' }),
    entry({ id: 's3', slug: 'data', title: 'Data flow', ord: 2, myState: 'unread' }),
    entry({ id: 's4', slug: 'scen', title: 'Scenarios', ord: 3, myState: 'skipped' }),
    entry({ id: 's5', slug: 'build', title: 'Build', ord: 4, status: 'generating' }),
    entry({ id: 's6', slug: 'deploy', title: 'Deploy', ord: 5, status: 'failed' }),
  ],
};

afterEach(cleanup);

describe('sectionGlyph', () => {
  it('maps states to the docs/05 glyph set', () => {
    expect(sectionGlyph(entry({ myState: 'completed' }), false)).toBe('✓');
    expect(sectionGlyph(entry({}), true)).toBe('●');
    expect(sectionGlyph(entry({ myState: 'unread' }), false)).toBe('○');
    expect(sectionGlyph(entry({ myState: 'read' }), false)).toBe('○');
    expect(sectionGlyph(entry({ myState: 'skipped' }), false)).toBe('─');
    expect(sectionGlyph(entry({ status: 'generating' }), false)).toBe('◐');
    expect(sectionGlyph(entry({ status: 'pending' }), false)).toBe('◐');
    expect(sectionGlyph(entry({ status: 'failed' }), false)).toBe('✗');
  });

  it('failed/generating win over user state and current', () => {
    expect(sectionGlyph(entry({ status: 'failed', myState: 'completed' }), true)).toBe('✗');
    expect(sectionGlyph(entry({ status: 'generating', myState: 'completed' }), true)).toBe('◐');
  });
});

describe('SectionNav', () => {
  function renderNav(currentSlug: string | null = 'arch') {
    return render(
      <MemoryRouter>
        <SectionNav toc={toc} sessionId="sess1" sessionTitle="next.js" currentSlug={currentSlug} />
      </MemoryRouter>,
    );
  }

  it('renders a row per section with its glyph', () => {
    renderNav();
    expect(screen.getByText('✓')).toBeInTheDocument(); // completed intro
    expect(screen.getByText('●')).toBeInTheDocument(); // current arch
    expect(screen.getByText('○')).toBeInTheDocument(); // unread data
    expect(screen.getByText('─')).toBeInTheDocument(); // skipped scenarios
    expect(screen.getByText('◐')).toBeInTheDocument(); // generating build
    expect(screen.getByText('✗')).toBeInTheDocument(); // failed deploy
  });

  it('links ready sections and disables generating/failed rows', () => {
    renderNav();
    expect(screen.getByRole('link', { name: /Data flow/ })).toHaveAttribute(
      'href',
      '/sessions/sess1/learn/data',
    );
    const generating = screen.getByText('Build').closest('span[aria-disabled]');
    expect(generating).not.toBeNull();
    expect(screen.queryByRole('link', { name: /Build/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Deploy/ })).not.toBeInTheDocument();
  });

  it('marks the current section and shows completion summary', () => {
    renderNav();
    expect(screen.getByRole('link', { name: /Architecture/ })).toHaveAttribute(
      'aria-current',
      'page',
    );
    // 1 of 6 completed; 5 min left from the single ready+unread section.
    expect(screen.getByText(/1\/6 completed/)).toBeInTheDocument();
    expect(screen.getByText(/~5 min left/)).toBeInTheDocument();
  });

  it('shows a quiz indicator and best-score tooltip on quiz sections', () => {
    renderNav();
    expect(screen.getByLabelText('Has a quiz')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /Intro/ })).toHaveAttribute(
      'title',
      'Best quiz score: 80%',
    );
  });
});
