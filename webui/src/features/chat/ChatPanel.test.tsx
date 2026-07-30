import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { Mock } from 'vitest';
import { ChatPanel } from './ChatPanel';
import { useChat } from '../../store/chat';
import type { KbStatus, QaMessage, QaThread, SessionSummary } from '../../api/types';

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
    getKb: vi.fn(),
    listThreads: vi.fn(),
    createThread: vi.fn(),
    listMessages: vi.fn(),
    sendMessage: vi.fn(),
    cancelMessage: vi.fn(),
    getChunk: vi.fn(),
  },
}));

import { api } from '../../api/client';

const mockApi = api as unknown as {
  getKb: Mock;
  listThreads: Mock;
  createThread: Mock;
  listMessages: Mock;
  sendMessage: Mock;
  cancelMessage: Mock;
  getChunk: Mock;
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
  progress: { completedSections: 0, totalSections: 5 },
};

const thread: QaThread = {
  id: 't1',
  title: 'Default thread',
  createdAt: '2026-07-30T00:00:00Z',
  lastMessageAt: null,
};

const kbReady: KbStatus = { embeddedChunks: 100, totalChunks: 100, ready: true };

function message(overrides: Partial<QaMessage>): QaMessage {
  return {
    id: 'm1',
    ord: 0,
    role: 'user',
    content: 'Hello',
    status: 'complete',
    citations: null,
    createdAt: '2026-07-30T00:01:00Z',
    ...overrides,
  };
}

function renderPanel(currentSectionId: string | null = 'sec1') {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <ChatPanel
        session={session}
        commitSha="abc1234def"
        currentSectionId={currentSectionId}
        onClose={() => {}}
      />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  useChat.setState({ panelOpen: false, buffers: {} });
  mockApi.getKb.mockResolvedValue(kbReady);
  mockApi.listThreads.mockResolvedValue([thread]);
  mockApi.listMessages.mockResolvedValue([]);
  mockApi.sendMessage.mockResolvedValue({ userMessageId: 'u1', assistantMessageId: 'a1' });
  mockApi.createThread.mockResolvedValue({ ...thread, id: 'tnew', title: 'Thread 2' });
  mockApi.cancelMessage.mockResolvedValue(undefined);
});

afterEach(cleanup);

describe('ChatPanel composer', () => {
  it('Enter sends the draft with sectionContext when the toggle is on (default)', async () => {
    renderPanel();
    const input = await screen.findByRole('textbox', { name: 'Message the expert' });
    fireEvent.change(input, { target: { value: 'How does routing work?' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(mockApi.sendMessage).toHaveBeenCalledTimes(1));
    expect(mockApi.sendMessage).toHaveBeenCalledWith('t1', {
      content: 'How does routing work?',
      sectionContext: 'sec1',
    });
  });

  it('omits sectionContext when the toggle is off', async () => {
    renderPanel();
    const input = await screen.findByRole('textbox', { name: 'Message the expert' });
    fireEvent.click(screen.getByRole('checkbox', { name: 'Include current section as context' }));
    fireEvent.change(input, { target: { value: 'What does this repo do?' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(mockApi.sendMessage).toHaveBeenCalledTimes(1));
    expect(mockApi.sendMessage).toHaveBeenCalledWith('t1', { content: 'What does this repo do?' });
  });

  it('creates the default thread lazily on first send when none exists', async () => {
    mockApi.listThreads.mockResolvedValue([]);
    renderPanel();
    const input = await screen.findByRole('textbox', { name: 'Message the expert' });
    fireEvent.change(input, { target: { value: 'Where should I start?' } });
    fireEvent.keyDown(input, { key: 'Enter' });
    await waitFor(() => expect(mockApi.sendMessage).toHaveBeenCalledTimes(1));
    expect(mockApi.createThread).toHaveBeenCalledWith('sess1');
    expect(mockApi.sendMessage).toHaveBeenCalledWith('tnew', {
      content: 'Where should I start?',
      sectionContext: 'sec1',
    });
  });
});

describe('ChatPanel messages', () => {
  it('renders a completed assistant message with citation cards', async () => {
    mockApi.listMessages.mockResolvedValue([
      message({ id: 'm1', ord: 0, role: 'user', content: 'How is routing resolved?' }),
      message({
        id: 'm2',
        ord: 1,
        role: 'assistant',
        content: 'Routing happens in the matcher [S1].',
        citations: [{ path: 'server/router.ts', startLine: 42, endLine: 78, chunkId: 'c1' }],
      }),
    ]);
    renderPanel();
    expect(await screen.findByText('server/router.ts')).toBeInTheDocument();
    expect(screen.getByText('L42–78')).toBeInTheDocument();
    expect(screen.getByLabelText('View on GitHub')).toHaveAttribute(
      'href',
      'https://github.com/vercel/next.js/blob/abc1234def/server/router.ts#L42-L78',
    );
    expect(screen.getByText(/Routing happens in the matcher/)).toBeInTheDocument();
  });

  it('shows Stop on a streaming message; clicking it POSTs cancel and the composer is locked', async () => {
    mockApi.listMessages.mockResolvedValue([
      message({ id: 'm1', ord: 0, role: 'user', content: 'Explain caching' }),
      message({ id: 'm2', ord: 1, role: 'assistant', content: 'Caching wor', status: 'streaming' }),
    ]);
    renderPanel();
    const stop = await screen.findByRole('button', { name: '◼ Stop' });
    expect(
      screen.getByText('The expert is answering — one question at a time.'),
    ).toBeInTheDocument();
    expect(screen.getByRole('textbox', { name: 'Message the expert' })).toBeDisabled();
    fireEvent.click(stop);
    await waitFor(() => expect(mockApi.cancelMessage).toHaveBeenCalledWith('m2'));
  });
});

describe('ChatPanel knowledge-base banner', () => {
  it('warns while the KB is still indexing', async () => {
    mockApi.getKb.mockResolvedValue({ embeddedChunks: 40, totalChunks: 100, ready: false });
    renderPanel();
    expect(
      await screen.findByText(/knowledge base is still indexing \(40\/100 chunks\)/),
    ).toBeInTheDocument();
  });

  it('shows no banner when the KB is ready', async () => {
    renderPanel();
    await screen.findByRole('textbox', { name: 'Message the expert' });
    expect(screen.queryByText(/knowledge base is still indexing/)).not.toBeInTheDocument();
  });
});
