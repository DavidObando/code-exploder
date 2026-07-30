import { Link } from 'react-router-dom';
import type { ExperienceToc, SectionTocEntry } from '../../api/types';
import styles from './tutorial.module.css';

// Glyphs per docs/05-ux.md: ✓ completed · ● current · ○ unread/read · ─ skipped
// (dimmed) · ◐ generating (pulsing, disabled) · ✗ failed. Pending shares ◐
// without the pulse.

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
}: {
  entry: SectionTocEntry;
  sessionId: string;
  isCurrent: boolean;
}) {
  const glyph = sectionGlyph(entry, isCurrent);
  const disabled = entry.status !== 'ready';
  const pulsing = entry.status === 'generating';
  const indent = { paddingLeft: `calc(var(--space-2) + ${entry.depth} * var(--space-4))` };
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
      {entry.status === 'ready' && entry.myState === 'unread' && (
        <span className={styles.tocMinutes}>{entry.estimatedMinutes}m</span>
      )}
    </>
  );

  if (disabled) {
    return (
      <span
        className={styles.tocRowDisabled}
        style={indent}
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
      style={indent}
      aria-current={isCurrent ? 'page' : undefined}
      title={quizTooltip}
    >
      {inner}
    </Link>
  );
}

/** Tutorial TOC: overall completion + estimated minutes left + per-section rows. */
export function SectionNav({
  toc,
  sessionId,
  sessionTitle,
  currentSlug,
}: {
  toc: ExperienceToc;
  sessionId: string;
  sessionTitle: string;
  currentSlug: string | null;
}) {
  const sections = [...toc.sections].sort((a, b) => a.ord - b.ord);
  const total = sections.length;
  const completed = sections.filter((s) => s.myState === 'completed').length;
  const percent = total > 0 ? Math.round((completed / total) * 100) : 0;
  const minutesLeft = sections
    .filter((s) => s.status === 'ready' && s.myState === 'unread')
    .reduce((sum, s) => sum + s.estimatedMinutes, 0);

  return (
    <nav className={styles.toc} aria-label="Tutorial contents">
      <div className={styles.tocHeader}>
        <h2 className={styles.tocTitle} title={sessionTitle}>
          {sessionTitle}
        </h2>
        <div className={styles.tocTrack} aria-hidden="true">
          <div className={styles.tocFill} style={{ width: `${percent}%` }} />
        </div>
        <div className={styles.tocMeta}>
          {completed}/{total} completed
          {minutesLeft > 0 && ` · ~${minutesLeft} min left`}
        </div>
      </div>
      {sections.map((entry) => (
        <Row
          key={entry.id}
          entry={entry}
          sessionId={sessionId}
          isCurrent={entry.slug === currentSlug}
        />
      ))}
    </nav>
  );
}
