import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SessionList } from './SessionList';
import { api } from '../../api/client';
import type { SessionSummary } from '../../api/types';

vi.mock('../../api/client', async () => {
  const actual = await vi.importActual<typeof import('../../api/client')>('../../api/client');
  return {
    ...actual,
    api: {
      ...actual.api,
      listSessions: vi.fn(),
      retrySession: vi.fn(),
      deleteSession: vi.fn(),
    },
  };
});

const mockedApi = vi.mocked(api);

function session(overrides: Partial<SessionSummary>): SessionSummary {
  return {
    id: '00000000-0000-0000-0000-000000000001',
    kind: 'repo',
    title: 'octo/repo',
    repoOwner: 'octo',
    repoName: 'repo',
    prNumber: null,
    status: 'ready',
    failureReason: null,
    createdAt: new Date().toISOString(),
    progress: { completedSections: 0, totalSections: 0 },
    analysisId: '00000000-0000-0000-0000-0000000000aa',
    ...overrides,
  };
}

function renderList() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <SessionList />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe('SessionList retry', () => {
  it('shows a retry control only on failed rows and posts the retry', async () => {
    const failed = session({ id: '00000000-0000-0000-0000-00000000000f', title: 'octo/broken', status: 'failed' });
    mockedApi.listSessions.mockResolvedValue([session({}), failed]);
    mockedApi.retrySession.mockResolvedValue(session({ ...failed, status: 'queued' }));

    renderList();

    const retryButton = await screen.findByRole('button', { name: 'Retry octo/broken' });
    expect(screen.queryByRole('button', { name: 'Retry octo/repo' })).not.toBeInTheDocument();

    fireEvent.click(retryButton);
    await waitFor(() =>
      expect(mockedApi.retrySession).toHaveBeenCalledWith('00000000-0000-0000-0000-00000000000f'),
    );
  });
});
