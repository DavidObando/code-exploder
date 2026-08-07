import { Link } from 'react-router-dom';
import { useEffect } from 'react';
import { useMutation } from '@tanstack/react-query';
import { api, ApiError } from '../../api/client';
import type { ExperienceToc, SectionTocEntry, SessionSummary } from '../../api/types';
import { useUi } from '../../store/ui';
import { useTutorial } from '../../store/tutorial';
import { useExplodeScope } from './useExplodeScope';
import {
  ancestorIds,
  buildTocTree,
  hasUnreadDescendant,
  isCollapsed,
  partitionMainTour,
  type TocNode,
} from './tocTree';
import styles from './tutorial.module.css';

// Glyphs per docs/05-ux.md: ✓ completed · ● current · ○ unread/read · ─ skipped
// (dimmed) · ◐ generating (pulsing, disabled) · ✗ failed. Pending shares ◐
// without the pulse. The small accent dot marks ready-but-never-viewed sections
// (rolling up onto collapsed parents), and winks out on scroll-to-end.

export function sectionGlyph(entry: SectionTocEntry, isCurrent: boolean): string {
  if (entry.status === 'failed') return '✗';
  if (entry.status === 'generating' || entry.status === 'pending') return '◐';
  if (isCurrent) return '●';
  if (entry.myState === 'completed') return '✓';
  if (entry.myState === 'skipped') return '─';
  return '○';
}

function glyphColor(entry: SectionTocEntry, isCurrent: boolean): string {
  if (entry.status === 'failed') return 'var(--error)';
  if (entry.status !== 'ready') return 'var(--text-muted)';
  if (isCurrent) return 'var(--accent)';
  if (entry.myState === 'completed') return 'var(--keep)';
  return 'var(--text-muted)';
}

function Row({
  entry,
  sessionId,
  isCurrent,
  showDot,
}: {
  entry: SectionTocEntry;
  sessionId: string;
  isCurrent: boolean;
  showDot: boolean;
}) {
  const glyph = sectionGlyph(entry, isCurrent);
  const disabled = entry.status !== 'ready';
  const pulsing = entry.status === 'generating';
  const quizTooltip =
    entry.hasQuiz && entry.quizBestPct !== null
      ? `Best quiz score: ${Math.round(entry.quizBestPct)}%`
      : entry.hasQuiz
        ? 'Has a quiz'
        : undefined;

  const inner = (
    <>
      <span
        className={pulsing ? styles.tocGlyphPulse : styles.tocGlyph}
        style={{ color: glyphColor(entry, isCurrent) }}
        aria-hidden="true"
      >
        {glyph}
      </span>
      <span className={styles.tocLabel}>{entry.title}</span>
      {entry.hasQuiz && (
        <span className={styles.tocQuizBadge} aria-label="Has a quiz">
          Q
        </span>
      )}
      <span className={styles.tocRight}>
        {showDot && <span className={styles.tocNewDot} aria-label="Unread" />}
        {entry.status === 'ready' && entry.myState === 'unread' && (
          <span className={styles.tocMinutes}>{entry.estimatedMinutes}m</span>
        )}
      </span>
    </>
  );

  if (disabled) {
    return (
      <span
        className={styles.tocRowDisabled}
        aria-disabled="true"
        title={entry.status === 'failed' ? 'This section failed to generate' : 'Still generating'}
      >
        {inner}
      </span>
    );
  }

  return (
    <Link
      to={`/sessions/${sessionId}/learn/${entry.slug}`}
      className={
        isCurrent
          ? styles.tocRowCurrent
          : entry.myState === 'skipped'
            ? styles.tocRowSkipped
            : styles.tocRow
      }
      aria-current={isCurrent ? 'page' : undefined}
      title={quizTooltip}
    >
      {inner}
    </Link>
  );
}

/** Inline retry on a failed deep-dive row (mirrors the failed-session pattern). */
function DiveRetryButton({ entry, sessionId }: { entry: SectionTocEntry; sessionId: string }) {
  const explode = useExplodeScope(sessionId);
  if (!entry.componentId) return null;
  return (
    <button
      type="button"
      className={styles.tocRetry}
      onClick={() => explode.mutate(entry.componentId!)}
      disabled={explode.isPending}
      aria-label={`Retry ${entry.title}`}
      title="Retry this deep dive"
    >
      ↻
    </button>
  );
}

function TreeRow({
  node,
  sessionId,
  currentSlug,
}: {
  node: TocNode;
  sessionId: string;
  currentSlug: string | null;
}) {
  const collapsedOverride = useTutorial((s) => s.collapsedOverride);
  const setCollapsed = useTutorial((s) => s.setCollapsed);
  const collapsed = isCollapsed(node.entry, collapsedOverride);
  const hasChildren = node.children.length > 0;
  const entry = node.entry;
  const ownDot = entry.status === 'ready' && entry.myState === 'unread';
  const rollupDot = hasChildren && collapsed && hasUnreadDescendant(node);

  return (
    <div>
      <div
        className={styles.tocRowWrap}
        style={{ paddingLeft: `calc(${entry.depth} * var(--space-4))` }}
      >
        {hasChildren ? (
          <button
            type="button"
            className={collapsed ? styles.tocDisclosureCollapsed : styles.tocDisclosure}
            aria-expanded={!collapsed}
            aria-label={`${collapsed ? 'Expand' : 'Collapse'} ${entry.title}`}
            onClick={() => setCollapsed(entry.id, !collapsed)}
          >
            ▾
          </button>
        ) : (
          <span className={styles.tocDisclosureSpacer} aria-hidden="true" />
        )}
        <Row
          entry={entry}
          sessionId={sessionId}
          isCurrent={entry.slug === currentSlug}
          showDot={ownDot || rollupDot}
        />
        {entry.kind === 'deep-dive' && entry.status === 'failed' && (
          <DiveRetryButton entry={entry} sessionId={sessionId} />
        )}
      </div>
      {hasChildren && !collapsed && (
        <div role="group">
          {node.children.map((child) => (
            <TreeRow
              key={child.entry.id}
              node={child}
              sessionId={sessionId}
              currentSlug={currentSlug}
            />
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Origin-story footer action (M9): repo sessions that finished analysis and
 * have no story sections yet can summon the historian.
 */
function StoryFooter({ session, hasStory }: { session: SessionSummary; hasStory: boolean }) {
  const toast = useUi((s) => s.toast);

  const start = useMutation({
    mutationFn: () => api.startStory(session.id),
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409) {
        toast('info', 'The story is already being told');
      } else {
        toast(
          'error',
          'Could not start the story',
          err instanceof ApiError ? err.message : 'Unexpected error',
        );
      }
    },
  });

  const eligible =
    session.kind === 'repo' &&
    (session.status === 'ready' || session.status === 'partial') &&
    !hasStory;
  if (!eligible) return null;

  // SectionReady invalidations bring the story sections into the TOC; once the
  // first one exists, hasStory flips and this footer disappears entirely.
  if (start.isSuccess) {
    return (
      <div className={styles.storyNote} role="status">
        the historian is digging through the archives…
      </div>
    );
  }

  return (
    <button
      className={styles.storyButton}
      onClick={() => start.mutate()}
      disabled={start.isPending}
    >
      ✨ Tell the origin story
    </button>
  );
}

/** Tutorial TOC: overall completion + estimated minutes left + the section tree. */
export function SectionNav({
  toc,
  session,
  currentSlug,
}: {
  toc: ExperienceToc;
  session: SessionSummary;
  currentSlug: string | null;
}) {
  const sessionId = session.id;
  const expandAll = useTutorial((s) => s.expandAll);
  const roots = buildTocTree(toc.sections);

  // Main-tour metrics stay stable when dives land; dives get their own line.
  const { mainTour, deepDive } = partitionMainTour(toc.sections);
  const total = mainTour.length;
  const completed = mainTour.filter((s) => s.myState === 'completed').length;
  const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
  const minutesLeft = mainTour
    .filter((s) => s.status === 'ready' && s.myState === 'unread')
    .reduce((sum, s) => sum + s.estimatedMinutes, 0);
  const diveCompleted = deepDive.filter((s) => s.myState === 'completed').length;
  const diveMinutes = deepDive
    .filter((s) => s.status === 'ready' && s.myState === 'unread')
    .reduce((sum, s) => sum + s.estimatedMinutes, 0);
  const hasStory = toc.sections.some((s) => s.kind === 'story');

  // Invariant: the current section's branch is always expanded — this single
  // effect covers deep links, reloads, and j/k walking into a collapsed branch.
  const hasCurrent = currentSlug !== null && toc.sections.some((s) => s.slug === currentSlug);
  useEffect(() => {
    if (!currentSlug || !hasCurrent) return;
    const sections = toc.sections;
    const current = sections.find((s) => s.slug === currentSlug);
    if (!current) return;
    const ids = ancestorIds(sections, current.id);
    if (current.kind === 'deep-dive') ids.push(current.id);
    if (ids.length > 0) expandAll(ids);
    // Keyed on the slug (not the toc object) so user collapses aren't fought.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentSlug, hasCurrent, expandAll]);

  return (
    <nav className={styles.toc} aria-label="Tutorial contents">
      <div className={styles.tocHeader}>
        <h2 className={styles.tocTitle} title={session.title}>
          {session.title}
        </h2>
        <div className={styles.tocTrack} aria-hidden="true">
          <div className={styles.tocFill} style={{ width: `${percent}%` }} />
        </div>
        <div className={styles.tocMeta}>
          {completed}/{total} completed
          {minutesLeft > 0 && ` · ~${minutesLeft} min left`}
        </div>
        {deepDive.length > 0 && (
          <div className={styles.tocMeta}>
            Deep dives: {diveCompleted}/{deepDive.length}
            {diveMinutes > 0 && ` · ~${diveMinutes} min`}
          </div>
        )}
        <Link to={`/sessions/${sessionId}/progress`} className={styles.tocVitalsLink}>
          Repository vitals ↗
        </Link>
      </div>
      {roots.map((node) => (
        <TreeRow
          key={node.entry.id}
          node={node}
          sessionId={sessionId}
          currentSlug={currentSlug}
        />
      ))}
      <StoryFooter session={session} hasStory={hasStory} />
    </nav>
  );
}
