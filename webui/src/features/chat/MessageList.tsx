import { useEffect, useRef } from 'react';
import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { QaMessage } from '../../api/types';
import { useChat } from '../../store/chat';
import type { GithubContext } from '../tutorial/blocks/CodeExcerpt';
import ui from '../../components/ui.module.css';
import blocks from '../tutorial/blocks/blocks.module.css';
import { CitationCard } from './CitationCard';
import styles from './chat.module.css';

/** Turns [Sn] markers into markdown links targeting their citation card. */
export function linkifyCitations(content: string, messageId: string): string {
  return content.replace(/\[S(\d+)\]/g, (_m, n: string) => `[S${n}](#cite-${messageId}-${n})`);
}

function StreamingMessage({
  message,
  onCancel,
  cancelPending,
}: {
  message: QaMessage;
  onCancel: (messageId: string) => void;
  cancelPending: boolean;
}) {
  const buffer = useChat((s) => s.buffers[message.id]);
  // Live tokens preferred; the row's ~1/s flushed content is the fallback.
  const text =
    buffer && buffer.text.length >= message.content.length ? buffer.text : message.content;

  return (
    <div className={styles.msgAssistant}>
      <div className={styles.msgLabel}>Expert</div>
      <span className={styles.streamText}>{text}</span>
      <span className={styles.caret} aria-hidden="true" />
      <div className={styles.stopRow}>
        <button
          className={ui.buttonGhost}
          onClick={() => onCancel(message.id)}
          disabled={cancelPending}
        >
          ◼ Stop
        </button>
      </div>
    </div>
  );
}

function CompletedMessage({
  message,
  analysisId,
  github,
}: {
  message: QaMessage;
  analysisId: string;
  github: GithubContext | null;
}) {
  const citations = message.citations ?? [];
  return (
    <div className={styles.msgAssistant}>
      <div className={styles.msgLabel}>Expert</div>
      <div className={blocks.markdown}>
        <ReactMarkdown
          remarkPlugins={[remarkGfm]}
          components={{
            a: ({ href, children }) => {
              if (href?.startsWith('#cite-')) {
                return (
                  <a
                    href={href}
                    onClick={(e) => {
                      e.preventDefault();
                      document
                        .getElementById(href.slice(1))
                        ?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
                    }}
                    style={{ color: 'var(--citation-gold)' }}
                  >
                    {children}
                  </a>
                );
              }
              return (
                <a href={href} target="_blank" rel="noreferrer">
                  {children}
                </a>
              );
            },
          }}
        >
          {linkifyCitations(message.content, message.id)}
        </ReactMarkdown>
      </div>
      {message.status === 'cancelled' && <div className={styles.stoppedNote}>Stopped.</div>}
      {message.status === 'error' && (
        <div className={styles.errorNote}>The expert hit an error answering this.</div>
      )}
      {citations.length > 0 && (
        <div className={styles.citations}>
          {citations.map((c, i) => (
            <CitationCard
              key={c.chunkId}
              citation={c}
              index={i}
              messageId={message.id}
              analysisId={analysisId}
              github={github}
            />
          ))}
        </div>
      )}
    </div>
  );
}

export function MessageList({
  messages,
  analysisId,
  github,
  onCancel,
  cancelPending,
}: {
  messages: QaMessage[];
  analysisId: string;
  github: GithubContext | null;
  onCancel: (messageId: string) => void;
  cancelPending: boolean;
}) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const streamingId = messages.find((m) => m.status === 'streaming')?.id ?? null;
  const streamLength = useChat((s) => (streamingId ? (s.buffers[streamingId]?.text.length ?? 0) : 0));

  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [messages.length, streamLength]);

  const ordered = [...messages].sort((a, b) => a.ord - b.ord);

  return (
    <div className={styles.messages} ref={scrollRef} role="log" aria-label="Conversation">
      {ordered.map((m) =>
        m.role === 'user' ? (
          <div key={m.id} className={styles.msgUser}>
            {m.content}
          </div>
        ) : m.status === 'streaming' ? (
          <StreamingMessage
            key={m.id}
            message={m}
            onCancel={onCancel}
            cancelPending={cancelPending}
          />
        ) : (
          <CompletedMessage key={m.id} message={m} analysisId={analysisId} github={github} />
        ),
      )}
    </div>
  );
}
