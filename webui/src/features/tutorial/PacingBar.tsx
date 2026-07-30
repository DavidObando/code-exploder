import { useCallback, useEffect } from 'react';
import { useNavigate } from 'react-router-dom';
import type { ExperienceToc } from '../../api/types';
import { useSectionProgress } from './useSectionProgress';
import ui from '../../components/ui.module.css';
import styles from './tutorial.module.css';

function isEditableTarget(target: EventTarget | null): boolean {
  return (
    target instanceof HTMLElement &&
    (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)
  );
}

/**
 * Sticky pacing bar: Skip section · Prev / Next · Mark complete. j/k and ←/→
 * step sections. `read` is auto-set by SectionView on scroll-to-end; here only
 * explicit skipped/completed transitions happen.
 */
export function PacingBar({
  toc,
  sessionId,
  currentSlug,
}: {
  toc: ExperienceToc;
  sessionId: string;
  currentSlug: string;
}) {
  const navigate = useNavigate();
  const progress = useSectionProgress(sessionId);

  const ordered = [...toc.sections].sort((a, b) => a.ord - b.ord);
  const index = ordered.findIndex((s) => s.slug === currentSlug);
  const current = index >= 0 ? ordered[index] : null;
  const prevEntry = ordered.slice(0, Math.max(index, 0)).reverse().find((s) => s.status === 'ready') ?? null;
  const nextEntry = index >= 0 ? ordered.slice(index + 1).find((s) => s.status === 'ready') ?? null : null;

  const go = useCallback(
    (slug: string | undefined) => {
      if (slug) navigate(`/sessions/${sessionId}/learn/${slug}`);
    },
    [navigate, sessionId],
  );

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (isEditableTarget(e.target)) return;
      if (e.key === 'j' || e.key === 'ArrowRight') go(nextEntry?.slug);
      else if (e.key === 'k' || e.key === 'ArrowLeft') go(prevEntry?.slug);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [go, nextEntry?.slug, prevEntry?.slug]);

  if (!current) return null;

  const completed = current.myState === 'completed';

  return (
    <div className={styles.pacingBar}>
      <button
        className={ui.buttonGhost}
        onClick={() => {
          progress.mutate({ sectionId: current.id, state: 'skipped' });
          go(nextEntry?.slug);
        }}
        disabled={progress.isPending}
      >
        Skip section
      </button>
      <span className={styles.pacingHint}>j / k to move</span>
      <div className={styles.pacingCenter}>
        <button
          className={ui.button}
          onClick={() => go(prevEntry?.slug)}
          disabled={!prevEntry}
          aria-label="Previous section"
        >
          ◀ Prev
        </button>
        <button
          className={completed ? ui.button : ui.buttonPrimary}
          onClick={() => progress.mutate({ sectionId: current.id, state: 'completed' })}
          disabled={completed || progress.isPending}
        >
          {completed ? '✓ Completed' : 'Mark complete'}
        </button>
        <button
          className={ui.button}
          onClick={() => go(nextEntry?.slug)}
          disabled={!nextEntry}
          aria-label="Next section"
        >
          Next ▶
        </button>
      </div>
    </div>
  );
}
