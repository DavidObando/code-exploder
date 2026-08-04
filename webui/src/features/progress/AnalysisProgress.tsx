import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import { useUi } from '../../store/ui';
import { ConfirmDialog } from '../../components/ConfirmDialog';
import type {
  AnalysisNarrationData,
  AnalysisProgressData,
} from '../../api/events';
import { liveEvents } from '../../services/liveEvents';
import { usePageTitle } from '../../lib/usePageTitle';
import { StageList } from './StageList';
import type { LiveStageProgress } from './StageList';
import { NarrationTicker } from './NarrationTicker';
import { RepoSummaryCard } from './RepoSummaryCard';
import ui from '../../components/ui.module.css';
import styles from './progress.module.css';

interface LiveNarrationLine {
  id: number;
  at: string;
  text: string;
}

export function AnalysisProgress() {
  const { id = '' } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const toast = useUi((s) => s.toast);
  const [confirmDelete, setConfirmDelete] = useState(false);

  const session = useQuery({
    queryKey: ['session', id],
    queryFn: () => api.getSession(id),
    enabled: id !== '',
  });

  const retry = useMutation({
    mutationFn: () => api.retrySession(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['session', id] });
      void queryClient.invalidateQueries({ queryKey: ['analysis', id] });
      void queryClient.invalidateQueries({ queryKey: ['sessions'] });
    },
    onError: (err) =>
      toast('error', 'Retry failed', err instanceof ApiError ? err.message : 'Unexpected error'),
  });

  const remove = useMutation({
    mutationFn: () => api.deleteSession(id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['sessions'] });
      navigate('/');
    },
    onError: (err) =>
      toast('error', 'Delete failed', err instanceof ApiError ? err.message : 'Unexpected error'),
  });

  const analysis = useQuery({
    queryKey: ['analysis', id],
    queryFn: () => api.getAnalysis(id),
    enabled: id !== '',
  });

  usePageTitle(session.data ? `${session.data.title} — Code Exploder` : 'Analysis — Code Exploder');

  // Stream-only render state (AnalysisProgress / AnalysisNarration). Never durable:
  // the snapshot query is the source of truth and refetches on lifecycle events.
  const [liveProgress, setLiveProgress] = useState<Record<string, LiveStageProgress>>({});
  const [liveNarration, setLiveNarration] = useState<LiveNarrationLine[]>([]);

  // Reset stream state when navigating between sessions.
  useEffect(() => {
    setLiveProgress({});
    setLiveNarration([]);
  }, [id]);

  // Snapshot lastEventId keeps GetEventsSince catch-ups small.
  useEffect(() => {
    if (id && analysis.data) liveEvents.noteSnapshot(id, analysis.data.lastEventId);
  }, [id, analysis.data]);

  // Join the session group (unsubscribes on unmount) and tap the stream.
  useEffect(() => {
    if (!id) return;
    return liveEvents.subscribe(id);
  }, [id]);

  useEffect(() => {
    if (!id) return;
    return liveEvents.listen((evt) => {
      if (evt.sessionId !== id) return;
      if (evt.kind === 'AnalysisProgress') {
        const data = evt.data as AnalysisProgressData;
        setLiveProgress((prev) => ({
          ...prev,
          [data.stage]: { percent: data.percent, detail: data.detail ?? null },
        }));
      } else if (evt.kind === 'AnalysisNarration') {
        const data = evt.data as AnalysisNarrationData;
        setLiveNarration((prev) =>
          prev.some((l) => l.id === evt.id)
            ? prev
            : [...prev, { id: evt.id, at: evt.at, text: data.text }],
        );
      }
    });
  }, [id]);

  if (session.isPending || analysis.isPending) {
    return <p className={styles.muted}>Loading analysis…</p>;
  }
  if (session.isError || analysis.isError || !session.data || !analysis.data) {
    return (
      <div className={styles.failureCard}>
        <h2 className={styles.failureTitle}>Session unavailable</h2>
        <p className={styles.failureReason}>
          This session could not be loaded. It may have been deleted.
        </p>
      </div>
    );
  }

  const snapshot = analysis.data;
  const status = snapshot.status;

  // Merge snapshot narration with live lines newer than the snapshot.
  const narration = [
    ...snapshot.narration,
    ...liveNarration
      .filter((l) => l.id > snapshot.lastEventId)
      .map((l) => ({ at: l.at, text: l.text })),
  ];

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <h1 className={styles.title}>{session.data.title}</h1>
        <span className={ui.chip}>
          {session.data.kind === 'pr' ? `PR #${session.data.prNumber}` : 'REPO'}
        </span>
        <span className={ui.chip}>{status}</span>
      </header>

      {status === 'ready' && snapshot.summary && <RepoSummaryCard summary={snapshot.summary} />}

      {(status === 'ready' || status === 'partial') && (
        <div className={styles.banner} role="status">
          <span className={styles.bannerGlyph} aria-hidden="true">
            ✓
          </span>
          <span>
            {status === 'ready' ? 'Analysis complete.' : 'Analysis finished with some failed sections.'}
          </span>
          <Link className={ui.buttonPrimary} to={`/sessions/${id}/learn`} style={{ marginLeft: 'auto' }}>
            Open the tutorial →
          </Link>
        </div>
      )}

      {status === 'failed' && (
        <div className={styles.failureCard}>
          <h2 className={styles.failureTitle}>Analysis failed</h2>
          <p className={styles.failureReason}>
            {session.data.failureReason ?? 'No failure reason was recorded.'}
          </p>
          <div className={styles.failureActions}>
            <button
              className={ui.buttonPrimary}
              onClick={() => retry.mutate()}
              disabled={retry.isPending}
            >
              ↻ Retry analysis
            </button>
            <button
              className={ui.buttonDangerSolid}
              onClick={() => setConfirmDelete(true)}
              disabled={remove.isPending}
            >
              ✕ Delete session
            </button>
          </div>
        </div>
      )}

      {confirmDelete && (
        <ConfirmDialog
          title="Delete session?"
          body={`"${session.data.title}" and its analysis will be removed. This cannot be undone.`}
          onCancel={() => setConfirmDelete(false)}
          onConfirm={() => {
            setConfirmDelete(false);
            remove.mutate();
          }}
        />
      )}

      <StageList stages={snapshot.stages} live={liveProgress} />
      <NarrationTicker lines={narration} />
    </div>
  );
}
