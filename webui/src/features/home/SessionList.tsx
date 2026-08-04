import { useState } from 'react';
import { Link } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import type { SessionStatus, SessionSummary } from '../../api/types';
import { relativeTime } from '../../lib/relativeTime';
import { useUi } from '../../store/ui';
import { ConfirmDialog } from '../../components/ConfirmDialog';
import styles from './home.module.css';

// Status glyphs per docs/05-ux.md: ● ready · ◐ in flight · ○ failed.
const statusGlyph: Record<SessionStatus, { glyph: string; color: string }> = {
  ready: { glyph: '●', color: 'var(--keep)' },
  partial: { glyph: '●', color: 'var(--suggestion)' },
  analyzing: { glyph: '◐', color: 'var(--accent)' },
  queued: { glyph: '◐', color: 'var(--text-muted)' },
  failed: { glyph: '○', color: 'var(--error)' },
};

function SessionRow({
  session,
  onDelete,
  onRetry,
  retrying,
}: {
  session: SessionSummary;
  onDelete: () => void;
  onRetry: () => void;
  retrying: boolean;
}) {
  const { glyph, color } = statusGlyph[session.status];
  const { completedSections, totalSections } = session.progress;
  // No sections exist yet in M1 ({0,0}); hide the completion bar rather than
  // showing a misleading empty (or full) bar.
  const showBar = totalSections > 0;
  const percent = showBar ? Math.round((completedSections / totalSections) * 100) : 0;

  return (
    <Link to={`/sessions/${session.id}`} className={styles.sessionRow}>
      <div className={styles.sessionHead}>
        <span className={styles.sessionGlyph} style={{ color }} aria-label={session.status}>
          {glyph}
        </span>
        <span className={styles.sessionTitle}>{session.title}</span>
      </div>
      <div className={styles.sessionMeta}>
        {session.status} · {relativeTime(session.createdAt)}
        {session.status === 'analyzing' && totalSections > 0 && (
          <> · writing sections ({totalSections} planned)</>
        )}
      </div>
      {showBar && (
        <div className={styles.miniTrack} aria-hidden="true">
          <div className={styles.miniFill} style={{ width: `${percent}%` }} />
        </div>
      )}
      {session.status === 'failed' && (
        <button
          className={styles.sessionRetry}
          aria-label={`Retry ${session.title}`}
          disabled={retrying}
          onClick={(e) => {
            e.preventDefault();
            e.stopPropagation();
            onRetry();
          }}
        >
          ↻
        </button>
      )}
      <button
        className={styles.sessionDelete}
        aria-label={`Delete ${session.title}`}
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          onDelete();
        }}
      >
        ✕
      </button>
    </Link>
  );
}

export function SessionList() {
  const queryClient = useQueryClient();
  const toast = useUi((s) => s.toast);
  const [pendingDelete, setPendingDelete] = useState<SessionSummary | null>(null);

  // Kept live by user-group hub events invalidating ['sessions'] (see liveEvents).
  const sessions = useQuery({
    queryKey: ['sessions'],
    queryFn: api.listSessions,
    retry: 1,
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteSession(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['sessions'] }),
    onError: (err) =>
      toast('error', 'Delete failed', err instanceof ApiError ? err.message : 'Unexpected error'),
  });

  const retry = useMutation({
    mutationFn: (id: string) => api.retrySession(id),
    onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['sessions'] }),
    onError: (err) =>
      toast('error', 'Retry failed', err instanceof ApiError ? err.message : 'Unexpected error'),
  });

  const sorted = [...(sessions.data ?? [])].sort(
    (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
  );

  return (
    <aside className={styles.sidebar} aria-label="Sessions">
      <h2 className={styles.sidebarTitle}>Sessions</h2>
      {sessions.isPending && <p className={styles.empty}>Loading…</p>}
      {sessions.isError && <p className={styles.empty}>Could not load sessions.</p>}
      {sessions.isSuccess && sorted.length === 0 && (
        <p className={styles.empty}>No sessions yet — explode your first codebase.</p>
      )}
      {sorted.map((s) => (
        <SessionRow
          key={s.id}
          session={s}
          onDelete={() => setPendingDelete(s)}
          onRetry={() => retry.mutate(s.id)}
          retrying={retry.isPending && retry.variables === s.id}
        />
      ))}
      {pendingDelete && (
        <ConfirmDialog
          title="Delete session?"
          body={`"${pendingDelete.title}" and its analysis will be removed. This cannot be undone.`}
          onCancel={() => setPendingDelete(null)}
          onConfirm={() => {
            remove.mutate(pendingDelete.id);
            setPendingDelete(null);
          }}
        />
      )}
    </aside>
  );
}
