import { useEffect, useRef } from 'react';
import styles from './progress.module.css';

/** Capped-height monospace log of narration lines; auto-scrolls to the newest. */
export function NarrationTicker({ lines }: { lines: { at: string; text: string }[] }) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = ref.current;
    if (el) el.scrollTop = el.scrollHeight;
  }, [lines.length]);

  return (
    <div className={styles.ticker} ref={ref} role="log" aria-label="Analysis narration">
      {lines.length === 0 && <div className={styles.tickerEmpty}>Waiting for the expert…</div>}
      {lines.map((line, i) => (
        <div key={`${line.at}-${i}`} className={styles.tickerLine}>
          › {line.text}
        </div>
      ))}
    </div>
  );
}
