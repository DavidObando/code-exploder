import type { RepoSummary } from '../../api/types';
import ui from '../../components/ui.module.css';
import styles from './RepoSummaryCard.module.css';

// Categorical ramp from the token palette; the trailing "other" bucket is muted.
const CHART_COLORS = [
  'var(--chart-1)',
  'var(--chart-2)',
  'var(--chart-3)',
  'var(--chart-4)',
  'var(--chart-5)',
  'var(--chart-6)',
];
const OTHER_COLOR = 'var(--chart-other)';
const MAX_LANGUAGES = 6;

const fmt = (n: number) => n.toLocaleString('en-US');

interface LangSegment {
  name: string;
  percent: number;
  color: string;
}

/** Top languages by percent, everything past the top 6 folded into "Other". */
export function languageSegments(languages: RepoSummary['languages']): LangSegment[] {
  const sorted = [...languages].sort((a, b) => b.percent - a.percent);
  const top = sorted.slice(0, MAX_LANGUAGES).map((lang, i) => ({
    name: lang.name,
    percent: lang.percent,
    color: CHART_COLORS[i],
  }));
  const rest = sorted.slice(MAX_LANGUAGES);
  if (rest.length > 0) {
    top.push({
      name: 'Other',
      percent: rest.reduce((sum, l) => sum + l.percent, 0),
      color: OTHER_COLOR,
    });
  }
  return top;
}

/**
 * "Repository vitals" — the M1 precursor of the M2 intro-section vitals card
 * (docs/05-ux.md). Rendered on the progress view once the run is ready.
 */
export function RepoSummaryCard({ summary }: { summary: RepoSummary }) {
  const segments = languageSegments(summary.languages);

  return (
    <section className={styles.card} aria-label="Repository vitals">
      <div className={styles.head}>
        <h2 className={styles.title}>Repository vitals</h2>
        <span className={styles.sha} title={summary.commitSha}>
          {summary.commitSha.slice(0, 7)}
        </span>
      </div>

      {summary.description && <p className={styles.description}>{summary.description}</p>}

      {segments.length > 0 && (
        <div>
          <div className={styles.langBar} role="img" aria-label="Language breakdown">
            {segments.map((seg) => (
              <div
                key={seg.name}
                className={styles.langSeg}
                data-testid="lang-segment"
                style={{ width: `${seg.percent}%`, background: seg.color }}
                title={`${seg.name} ${seg.percent.toFixed(1)}%`}
              />
            ))}
          </div>
          <div className={styles.legend}>
            {segments.map((seg) => (
              <span key={seg.name} className={styles.legendItem}>
                <span
                  className={styles.legendSwatch}
                  style={{ background: seg.color }}
                  aria-hidden="true"
                />
                {seg.name}{' '}
                <span className={styles.legendPercent}>{seg.percent.toFixed(1)}%</span>
              </span>
            ))}
          </div>
        </div>
      )}

      <div className={styles.statRow}>
        <div className={styles.stat}>
          <span className={styles.statValue}>{fmt(summary.analyzedFileCount)}</span>
          <span className={styles.statLabel}>files analyzed</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{fmt(summary.excludedFileCount)}</span>
          <span className={styles.statLabel}>excluded</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{fmt(summary.chunkCount)}</span>
          <span className={styles.statLabel}>chunks</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{fmt(summary.commitCount)}</span>
          <span className={styles.statLabel}>commits</span>
        </div>
        <div className={styles.stat}>
          <span className={styles.statValue}>{fmt(summary.contributorCount)}</span>
          <span className={styles.statLabel}>contributors</span>
        </div>
      </div>

      {(summary.buildSystems.length > 0 || summary.ciConfigs.length > 0) && (
        <div className={styles.chipRow}>
          {summary.buildSystems.length > 0 && (
            <>
              <span className={styles.chipRowLabel}>Build</span>
              {summary.buildSystems.map((b) => (
                <span key={b} className={ui.chip}>
                  {b}
                </span>
              ))}
            </>
          )}
          {summary.ciConfigs.length > 0 && (
            <>
              <span className={styles.chipRowLabel}>CI</span>
              {summary.ciConfigs.map((c) => (
                <span key={c} className={ui.chip}>
                  {c}
                </span>
              ))}
            </>
          )}
        </div>
      )}

      <div className={styles.columns}>
        {summary.components.length > 0 && (
          <div>
            <h3 className={styles.listTitle}>Components</h3>
            <ul className={styles.monoList}>
              {summary.components.slice(0, 8).map((c) => (
                <li key={c.name} className={styles.monoRow}>
                  <span className={styles.monoName}>{c.name}</span>
                  <span className={styles.monoCount}>{fmt(c.fileCount)} files</span>
                </li>
              ))}
            </ul>
          </div>
        )}
        {summary.topChurnFiles.length > 0 && (
          <div>
            <h3 className={styles.listTitle}>Top churn files</h3>
            <ul className={styles.monoList}>
              {summary.topChurnFiles.slice(0, 5).map((f) => (
                <li key={f.path} className={styles.monoRow}>
                  <span className={styles.monoName}>{f.path}</span>
                  <span className={styles.monoCount}>{fmt(f.commits)} commits</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </section>
  );
}
