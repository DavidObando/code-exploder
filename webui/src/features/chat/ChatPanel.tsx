import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import type { QaMessage, SendMessageRequest, SessionSummary } from '../../api/types';
import { useChat } from '../../store/chat';
import { useUi } from '../../store/ui';
import type { GithubContext } from '../tutorial/blocks/CodeExcerpt';
import { MessageList } from './MessageList';
import { Composer } from './Composer';
import styles from './chat.module.css';

const PROMPT_IDEAS = [
  'What does this repo do?',
  'How is the code organized?',
  'Where should I start reading?',
];

/**
 * "Ask the expert" slide-over (docs/05 §Q&A chat panel). One default thread,
 * created lazily on first send; simple dropdown for more threads.
 */
export function ChatPanel({
  session,
  commitSha,
  currentSectionId,
  onClose,
}: {
  session: SessionSummary;
  commitSha: string;
  currentSectionId: string | null;
  onClose: () => void;
}) {
  const sessionId = session.id;
  const queryClient = useQueryClient();
  const toast = useUi((s) => s.toast);

  const threads = useQuery({
    queryKey: ['threads', sessionId],
    queryFn: () => api.listThreads(sessionId),
  });

  const [selectedThreadId, setSelectedThreadId] = useState<string | null>(null);
  const activeThreadId = selectedThreadId ?? threads.data?.[0]?.id ?? null;

  // KB status: fetched on open (panel mounts only while open), polled at 10s
  // until ready.
  const kb = useQuery({
    queryKey: ['kb', sessionId],
    queryFn: () => api.getKb(sessionId),
    refetchInterval: (query) => (query.state.data && !query.state.data.ready ? 10_000 : false),
  });

  const messages = useQuery({
    queryKey: ['thread', activeThreadId ?? 'none'],
    queryFn: () => api.listMessages(activeThreadId!),
    enabled: activeThreadId !== null,
  });

  const [draft, setDraft] = useState('');
  const [includeContext, setIncludeContext] = useState(true);

  const streaming = messages.data?.find((m) => m.status === 'streaming') ?? null;

  // Seq-gap resync: the buffer flags a missed flush; refetch the thread and
  // heal the buffer from the row's flushed content.
  const needsResync = useChat((s) =>
    streaming ? (s.buffers[streaming.id]?.needsResync ?? false) : false,
  );
  useEffect(() => {
    if (!needsResync || !streaming || !activeThreadId) return;
    let cancelled = false;
    api
      .listMessages(activeThreadId)
      .then((rows) => {
        if (cancelled) return;
        queryClient.setQueryData<QaMessage[]>(['thread', activeThreadId], rows);
        const row = rows.find((m) => m.id === streaming.id);
        useChat.getState().resyncBuffer(streaming.id, row?.content ?? '');
      })
      .catch(() => {
        if (!cancelled) useChat.getState().resyncBuffer(streaming.id, '');
      });
    return () => {
      cancelled = true;
    };
  }, [needsResync, streaming, activeThreadId, queryClient]);

  const send = useMutation({
    mutationFn: async (content: string) => {
      let threadId = activeThreadId;
      if (!threadId) {
        const thread = await api.createThread(sessionId);
        threadId = thread.id;
        setSelectedThreadId(thread.id);
        void queryClient.invalidateQueries({ queryKey: ['threads', sessionId] });
      }
      const body: SendMessageRequest = {
        content,
        ...(includeContext && currentSectionId ? { sectionContext: currentSectionId } : {}),
      };
      const res = await api.sendMessage(threadId, body);
      return { threadId, ...res };
    },
    onSuccess: ({ threadId }) => {
      setDraft('');
      void queryClient.invalidateQueries({ queryKey: ['thread', threadId] });
    },
    onError: (err) => {
      const conflict = err instanceof ApiError && err.status === 409;
      toast(
        conflict ? 'warning' : 'error',
        conflict ? 'The expert is still answering' : 'Could not send',
        err instanceof ApiError ? err.message : 'Unexpected error',
      );
    },
  });

  const newThread = useMutation({
    mutationFn: () => api.createThread(sessionId),
    onSuccess: (thread) => {
      setSelectedThreadId(thread.id);
      void queryClient.invalidateQueries({ queryKey: ['threads', sessionId] });
    },
    onError: () => toast('error', 'Could not create a new thread'),
  });

  const cancel = useMutation({
    mutationFn: (messageId: string) => api.cancelMessage(messageId),
    onError: () => toast('error', 'Could not stop the answer'),
  });

  const inFlight = streaming !== null || send.isPending;
  const github: GithubContext | null = commitSha
    ? { owner: session.repoOwner, repo: session.repoName, commitSha }
    : null;
  const showEmpty = activeThreadId === null || (messages.isSuccess && messages.data.length === 0);

  return (
    <aside className={styles.panel} aria-label="Ask the expert">
      <div className={styles.header}>
        <h2 className={styles.headerTitle}>Ask the expert</h2>
        {threads.isSuccess && threads.data.length > 1 && (
          <select
            className={styles.threadSelect}
            aria-label="Thread"
            value={activeThreadId ?? ''}
            onChange={(e) => setSelectedThreadId(e.target.value)}
          >
            {threads.data.map((t) => (
              <option key={t.id} value={t.id}>
                {t.title}
              </option>
            ))}
          </select>
        )}
        <button
          className={styles.headerButton}
          onClick={() => newThread.mutate()}
          disabled={newThread.isPending}
          title="Start a fresh context"
        >
          + New thread
        </button>
        <button className={styles.headerButton} onClick={onClose} aria-label="Close chat">
          ✕
        </button>
      </div>

      {kb.data && !kb.data.ready && (
        <div className={styles.kbBanner} role="status">
          <span className={styles.kbGlyph} aria-hidden="true">
            ◐
          </span>
          The knowledge base is still indexing ({kb.data.embeddedChunks}/{kb.data.totalChunks}{' '}
          chunks) — answers may be incomplete.
        </div>
      )}

      {showEmpty ? (
        <div className={styles.messages}>
          <div className={styles.empty}>
            <p className={styles.emptyTitle}>Ask anything about this codebase</p>
            <p style={{ margin: 0 }}>
              The expert has read every file at the analyzed commit and cites its sources.
            </p>
            <div className={styles.chipRow}>
              {PROMPT_IDEAS.map((idea) => (
                <button key={idea} className={styles.promptChip} onClick={() => setDraft(idea)}>
                  {idea}
                </button>
              ))}
            </div>
          </div>
        </div>
      ) : (
        <MessageList
          messages={messages.data ?? []}
          analysisId={session.analysisId}
          github={github}
          onCancel={(id) => cancel.mutate(id)}
          cancelPending={cancel.isPending}
        />
      )}

      <Composer
        value={draft}
        onChange={setDraft}
        onSend={() => {
          const content = draft.trim();
          if (content && !inFlight) send.mutate(content);
        }}
        disabled={inFlight}
        disabledNote="The expert is answering — one question at a time."
        includeContext={includeContext}
        onToggleContext={setIncludeContext}
        contextAvailable={currentSectionId !== null}
      />
    </aside>
  );
}
