import type { CalloutData } from '../../../api/types';
import { MarkdownBlock } from './MarkdownBlock';
import styles from './blocks.module.css';

const variantStyle: Record<CalloutData['variant'], { color: string; glyph: string }> = {
  insight: { color: 'var(--accent)', glyph: '◆' },
  warning: { color: 'var(--suggestion)', glyph: '▲' },
  convention: { color: 'var(--keep)', glyph: '§' },
};

/** Left-accent card, colored by variant. */
export function Callout({ data }: { data: CalloutData }) {
  const { color, glyph } = variantStyle[data.variant];
  return (
    <aside className={styles.callout} style={{ borderLeftColor: color }} data-variant={data.variant}>
      <div className={styles.calloutTitle}>
        <span className={styles.calloutGlyph} style={{ color }} aria-hidden="true">
          {glyph}
        </span>
        {data.title}
      </div>
      <MarkdownBlock md={data.md} />
    </aside>
  );
}
