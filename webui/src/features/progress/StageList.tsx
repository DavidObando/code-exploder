import type { StageInfo } from '../../api/types';
import styles from './progress.module.css';

export interface LiveStageProgress {
  percent: number;
  detail: string | null;
}

const glyphClass = {
  done: styles.stageGlyphDone,
  active: styles.stageGlyphActive,
  pending: styles.stageGlyphPending,
  failed: styles.stageGlyphFailed,
} as const;

const glyph = { done: '✓', active: '▸', pending: '○', failed: '✗' } as const;

/**
 * Vertical stage list. `live` carries AnalysisProgress stream overlays keyed by
 * stage key — direct render only, never written back to server state.
 */
export function StageList({
  stages,
  live = {},
}: {
  stages: StageInfo[];
  live?: Record<string, LiveStageProgress>;
}) {
  return (
    <ol className={styles.stageList}>
      {stages.map((stage) => {
        const overlay = stage.state === 'active' ? live[stage.key] : undefined;
        const percent = overlay?.percent ?? stage.percent;
        const detail = overlay?.detail ?? stage.detail;
        return (
          <li key={stage.key} className={styles.stage} data-state={stage.state}>
            <span className={glyphClass[stage.state]} aria-hidden="true">
              {glyph[stage.state]}
            </span>
            <div className={styles.stageBody}>
              <div className={styles.stageLabelRow}>
                <span
                  className={
                    stage.state === 'pending' ? styles.stageLabelPending : styles.stageLabel
                  }
                >
                  {stage.label}
                </span>
                {stage.state === 'active' && percent !== null && (
                  <span className={styles.stagePercent}>{Math.round(percent)}%</span>
                )}
              </div>
              {stage.state === 'active' && (
                <div
                  className={styles.stageTrack}
                  role="progressbar"
                  aria-label={stage.label}
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={percent === null ? undefined : Math.round(percent)}
                >
                  <div className={styles.stageFill} style={{ width: `${percent ?? 0}%` }} />
                </div>
              )}
              {detail && <span className={styles.stageDetail}>{detail}</span>}
            </div>
          </li>
        );
      })}
    </ol>
  );
}
