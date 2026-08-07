import { useEffect, useMemo, useRef } from 'react';
import { Navigate, useParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { usePageTitle } from '../../lib/usePageTitle';
import { QuizCard } from '../quiz/QuizCard';
import { BlockRenderer } from './blocks/BlockRenderer';
import { GoDeeperCard } from './GoDeeperCard';
import { useExplodeScope } from './useExplodeScope';
import { useSectionProgress } from './useSectionProgress';
import { useTutorialContext } from './TutorialLayout';
import { buildTocTree, flattenAll } from './tocTree';
import ui from '../../components/ui.module.css';
import styles from './tutorial.module.css';

/** Index route: jump to the first non-completed ready section, in tree order. */
export function TutorialIndexRedirect() {
  const { toc, session } = useTutorialContext();
  const ordered = flattenAll(buildTocTree(toc.sections));
  const target =
    ordered.find((s) => s.status === 'ready' && s.myState !== 'completed') ??
    ordered.find((s) => s.status === 'ready');

  if (!target) {
    return (
      <div className={styles.placeholder}>
        The expert is writing the first sections — they'll appear here as they're ready.
      </div>
    );
  }
  return <Navigate to={`/sessions/${session.id}/learn/${target.slug}`} replace />;
}

export function SectionView() {
  const { sectionSlug = '' } = useParams<{ sectionSlug: string }>();
  const { toc, session } = useTutorialContext();
  const progress = useSectionProgress(session.id);

  const entry = toc.sections.find((s) => s.slug === sectionSlug) ?? null;
  usePageTitle(entry ? `${entry.title} — Code Exploder` : `${session.title} — Code Exploder`);

  const ready = entry?.status === 'ready';
  const section = useQuery({
    queryKey: ['section', entry?.id ?? 'missing'],
    queryFn: () => api.getSection(entry!.id),
    enabled: entry !== null && ready,
  });

  const articleRef = useRef<HTMLElement>(null);

  // Scroll-to-end auto-marks `read` — an upgrade from unread only, never a
  // downgrade of read/skipped/completed.
  const sentinelRef = useRef<HTMLDivElement>(null);
  const mutateRef = useRef(progress.mutate);
  mutateRef.current = progress.mutate;
  const autoReadSectionId =
    entry !== null && ready && entry.myState === 'unread' ? entry.id : null;
  useEffect(() => {
    const el = sentinelRef.current;
    if (!el || !autoReadSectionId || typeof IntersectionObserver === 'undefined') return;
    const io = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) {
        io.disconnect();
        mutateRef.current({ sectionId: autoReadSectionId, state: 'read' });
      }
    });
    io.observe(el);
    return () => io.disconnect();
  }, [autoReadSectionId]);

  const blocks = useMemo(
    () => [...(section.data?.blocks ?? [])].sort((a, b) => a.ord - b.ord),
    [section.data],
  );
  const firstDiagramId = blocks.find((b) => b.type === 'diagram')?.id ?? null;

  if (!entry) {
    return <div className={styles.placeholder}>No such section in this tutorial.</div>;
  }
  if (entry.status === 'failed') {
    if (entry.kind === 'deep-dive') {
      return <FailedDivePlaceholder entry={entry} sessionId={session.id} />;
    }
    return (
      <div className={styles.placeholder}>
        This section failed to generate. The rest of the tutorial is unaffected.
      </div>
    );
  }
  if (!ready) {
    return (
      <div className={styles.placeholder}>
        The expert is still writing "{entry.title}" — it will pop in when ready.
      </div>
    );
  }
  if (section.isPending) {
    return <div className={styles.placeholder}>Loading section…</div>;
  }
  if (section.isError || !section.data) {
    return <div className={styles.placeholder}>This section could not be loaded.</div>;
  }

  const github = {
    owner: session.repoOwner,
    repo: session.repoName,
    commitSha: toc.commitSha,
  };
  // Tree order, not max ord — after a dive, the highest ord is a nested leaf.
  const isLast = flattenAll(buildTocTree(toc.sections)).at(-1)?.id === entry.id;
  const showGoDeeper = entry.kind === 'architecture' || entry.kind === 'deep-dive';

  return (
    <article ref={articleRef}>
      <h1 className={styles.sectionTitle}>{entry.title}</h1>
      <p className={styles.sectionKind}>{entry.kind}</p>
      <div className={styles.blocks}>
        {blocks.map((block) => (
          <BlockRenderer
            key={block.id}
            block={block}
            github={github}
            firstDiagramId={firstDiagramId}
          />
        ))}
      </div>
      {section.data.quizId && (
        <QuizCard
          quizId={section.data.quizId}
          onReread={() => articleRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' })}
        />
      )}
      {showGoDeeper && <GoDeeperCard toc={toc} sessionId={session.id} entry={entry} />}
      {isLast && (
        <div className={styles.endCard}>
          <p className={styles.endCardTitle}>You've reached the end</p>
          <p style={{ margin: 0 }}>That's the whole tour of {session.title}. Nicely done.</p>
        </div>
      )}
      <div ref={sentinelRef} aria-hidden="true" />
    </article>
  );
}

/** A failed deep dive gets an inline retry — the session itself is unaffected. */
function FailedDivePlaceholder({
  entry,
  sessionId,
}: {
  entry: { title: string; componentId: string | null };
  sessionId: string;
}) {
  const explode = useExplodeScope(sessionId);
  return (
    <div className={styles.placeholder}>
      <p>“{entry.title}” failed to generate. The rest of the tutorial is unaffected.</p>
      {entry.componentId && (
        <button
          type="button"
          className={ui.buttonPrimary}
          onClick={() => explode.mutate(entry.componentId!)}
          disabled={explode.isPending}
        >
          ↻ Retry this deep dive
        </button>
      )}
    </div>
  );
}
