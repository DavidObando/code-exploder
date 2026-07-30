import ReactMarkdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import styles from './blocks.module.css';

/** GFM markdown prose, styled by design tokens. */
export function MarkdownBlock({ md }: { md: string }) {
  return (
    <div className={styles.markdown}>
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{md}</ReactMarkdown>
    </div>
  );
}
