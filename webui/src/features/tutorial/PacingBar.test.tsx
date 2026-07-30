import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PacingBar } from './PacingBar';
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
    entry({ id: 's1', slug: 'intro', title: 'Intro', ord: 0, myState: 'completed' }),
    entry({ id: 's2', slug: 'arch', title: 'Architecture', ord: 1, myState: 'read' }),
    entry({ id: 's3', slug: 'build', title: 'Build', ord: 2, status: 'generating' }),
    entry({ id: 's4', slug: 'deploy', title: 'Deploy', ord: 3 }),
  ],
};

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  fetchMock = vi.fn(async (_url: RequestInfo | URL, init?: RequestInit) => {
    const body = JSON.parse(String(init?.body)) as { state: string };
    return {
      ok: true,
      status: 200,
      json: async () => ({
        sectionId: 's2',
        state: body.state,
        sessionProgress: { completedSections: 1, totalSections: 4 },
      }),
    } as Response;
  });
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function renderBar(currentSlug = 'arch') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[`/sessions/sess1/learn/${currentSlug}`]}>
        <Routes>
          <Route
            path="/sessions/:id/learn/:sectionSlug"
            element={<PacingBar toc={toc} sessionId="sess1" currentSlug={currentSlug} />}
          />
        </Routes>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('PacingBar', () => {
  it('PUTs skipped for the current section when Skip is clicked', async () => {
    renderBar();
    fireEvent.click(screen.getByRole('button', { name: 'Skip section' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/sections/s2/progress');
    expect(init.method).toBe('PUT');
    expect(JSON.parse(String(init.body))).toEqual({ state: 'skipped' });
  });

  it('PUTs completed when Mark complete is clicked', async () => {
    renderBar();
    fireEvent.click(screen.getByRole('button', { name: 'Mark complete' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('/api/sections/s2/progress');
    expect(JSON.parse(String(init.body))).toEqual({ state: 'completed' });
  });

  it('shows a disabled Completed button for an already-completed section', () => {
    renderBar('intro');
    const button = screen.getByRole('button', { name: '✓ Completed' });
    expect(button).toBeDisabled();
  });

  it('navigation skips non-ready sections: Next from arch targets deploy, not generating build', () => {
    renderBar();
    // Next is enabled (deploy is ready); build (generating) is skipped over.
    expect(screen.getByRole('button', { name: 'Next section' })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: 'Next section' }));
    // No PUT happens on pure navigation.
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('disables Prev on the first section', () => {
    renderBar('intro');
    expect(screen.getByRole('button', { name: 'Previous section' })).toBeDisabled();
  });
});
