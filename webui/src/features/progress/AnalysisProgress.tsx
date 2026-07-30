import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import type {
  AnalysisNarrationData,
  AnalysisProgressData,
} from '../../api/events';
import { liveEvents } from '../../services/liveEvents';
import { usePageTitle } from '../../lib/usePageTitle';
import { StageList } from './StageList';
import type { LiveStageProgress } from './StageList';
import { NarrationTicker } from './NarrationTicker';
import ui from '../../components/ui.module.css';
import styles from './progress.module.css';

interface LiveNarrationLine {
  id: number;
  at: string;
  text: string;
}

export function AnalysisProgress() {
  const { id = '' } = useParams<{ id: string }>();

  const session = useQuery({
    queryKey: ['session', id],
    queryFn: () => api.getSession(id),
    enabled: id !== '',
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

      {status === 'ready' && (
        <div className={styles.banner} role="status">
          <span className={styles.bannerGlyph} aria-hidden="true">
            ✓
          </span>
          Analysis complete — the tutorial experience arrives in M2.
        </div>
      )}

      {status === 'failed' && (
        <div className={styles.failureCard}>
          <h2 className={styles.failureTitle}>Analysis failed</h2>
          <p className={styles.failureReason}>
            {session.data.failureReason ?? 'No failure reason was recorded.'}
          </p>
        </div>
      )}

      <StageList stages={snapshot.stages} live={liveProgress} />
      <NarrationTicker lines={narration} />
    </div>
  );
}
