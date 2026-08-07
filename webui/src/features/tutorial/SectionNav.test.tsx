import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Mock } from 'vitest';
import { SectionNav, sectionGlyph } from './SectionNav';
import { useUi } from '../../store/ui';
import type { ExperienceToc, SectionTocEntry, SessionSummary } from '../../api/types';

vi.mock('../../api/client', () => ({
  ApiError: class MockApiError extends Error {
    constructor(
      public status: number,
      message: string,
    ) {
      super(message);
    }
  },
  api: {
    startStory: vi.fn(),
  },
}));

import { api, ApiError } from '../../api/client';

const mockStartStory = api.startStory as Mock;

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
    componentId: null,
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

const session: SessionSummary = {
  id: 'sess1',
  kind: 'repo',
  title: 'next.js',
  repoOwner: 'vercel',
  repoName: 'next.js',
  prNumber: null,
  analysisId: 'an1',
  status: 'ready',
  failureReason: null,
  createdAt: '2026-07-30T00:00:00Z',
  progress: { completedSections: 1, totalSections: 6 },
};

beforeEach(() => {
  vi.clearAllMocks();
  useUi.setState({ toasts: [] });
  mockStartStory.mockResolvedValue(undefined);
});

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

function renderNav(
  currentSlug: string | null = 'arch',
  navToc: ExperienceToc = toc,
  navSession: SessionSummary = session,
) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <SectionNav toc={navToc} session={navSession} currentSlug={currentSlug} />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('SectionNav', () => {

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

describe('SectionNav story footer', () => {
  const storyButtonName = /Tell the origin story/;

  it('renders for a ready repo session with no story sections and POSTs on click', async () => {
    renderNav();
    const button = screen.getByRole('button', { name: storyButtonName });
    fireEvent.click(button);
    await waitFor(() => expect(mockStartStory).toHaveBeenCalledWith('sess1'));
    // Button flips to the pulsing historian note until story sections arrive.
    expect(await screen.findByText('the historian is digging through the archives…')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: storyButtonName })).not.toBeInTheDocument();
  });

  it('does not render for PR sessions', () => {
    renderNav('arch', toc, { ...session, kind: 'pr', prNumber: 812 });
    expect(screen.queryByRole('button', { name: storyButtonName })).not.toBeInTheDocument();
  });

  it('does not render while the session is still analyzing', () => {
    renderNav('arch', toc, { ...session, status: 'analyzing' });
    expect(screen.queryByRole('button', { name: storyButtonName })).not.toBeInTheDocument();
  });

  it('does not render once story sections exist in the TOC', () => {
    const withStory: ExperienceToc = {
      ...toc,
      sections: [
        ...toc.sections,
        entry({ id: 's7', slug: 'origins', title: 'Origins', ord: 6, kind: 'story' }),
      ],
    };
    renderNav('arch', withStory);
    expect(screen.queryByRole('button', { name: storyButtonName })).not.toBeInTheDocument();
    expect(screen.queryByText(/historian/)).not.toBeInTheDocument();
  });

  it('shows an info toast on 409 (story already being told)', async () => {
    mockStartStory.mockRejectedValue(new ApiError(409, 'Story generation already running'));
    renderNav();
    fireEvent.click(screen.getByRole('button', { name: storyButtonName }));
    await waitFor(() => {
      const toasts = useUi.getState().toasts;
      expect(toasts.some((t) => t.kind === 'info' && t.title === 'The story is already being told')).toBe(true);
    });
    // 409 keeps the button (the TOC will update via SectionReady invalidations).
    expect(screen.getByRole('button', { name: storyButtonName })).toBeInTheDocument();
  });
});
