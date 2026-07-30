import type { CodeBlockData } from '../../../api/types';
import { MarkdownBlock } from './MarkdownBlock';
import styles from './blocks.module.css';

export interface GithubContext {
  owner: string;
  repo: string;
  commitSha: string;
}

export type DiffLineKind = 'added' | 'removed' | 'hunk' | 'context';

/** Classifies one unified-diff line for tinting. */
export function classifyDiffLine(line: string): DiffLineKind {
  if (line.startsWith('@@')) return 'hunk';
  if (line.startsWith('+')) return 'added';
  if (line.startsWith('-')) return 'removed';
  return 'context';
}

const diffLineClass: Record<DiffLineKind, string> = {
  added: styles.codeLineAdded,
  removed: styles.codeLineRemoved,
  hunk: styles.codeLineHunk,
  context: styles.codeLine,
};

/**
 * Code excerpt: monospace block, line numbers from startLine, SHA-pinned
 * "View on GitHub" deep link. No client-side highlighting in M2 (kept the
 * bundle lean); shiki can slot in here later.
 *
 * language === 'Diff' (PR walkthroughs, M5): unified-diff hunks get subtle
 * token-derived add/remove tints and a muted @@ header. Line numbers still
 * count from startLine over all hunk lines — the new-file position of the hunk
 * start; approximate for deleted lines, which is accepted.
 */
export function CodeExcerpt({ data, github }: { data: CodeBlockData; github: GithubContext | null }) {
  const lines = data.content.replace(/\n$/, '').split('\n');
  const isDiff = data.language === 'Diff';
  const href = github
    ? `https://github.com/${github.owner}/${github.repo}/blob/${github.commitSha}/${data.path}#L${data.startLine}-L${data.endLine}`
    : null;

  return (
    <figure className={styles.codeBlock}>
      <div className={styles.codeHeader}>
        <span className={styles.codePath} title={data.path}>
          {data.path}
        </span>
        {href && (
          <a className={styles.codeGithubLink} href={href} target="_blank" rel="noreferrer">
            View on GitHub ↗
          </a>
        )}
      </div>
      <pre className={styles.codePre} data-language={data.language}>
        <code>
          {lines.map((line, i) => {
            const kind = isDiff ? classifyDiffLine(line) : 'context';
            return (
              <span
                key={i}
                className={isDiff ? diffLineClass[kind] : styles.codeLine}
                data-diff={isDiff ? kind : undefined}
              >
                <span className={styles.lineNo} aria-hidden="true">
                  {data.startLine + i}
                </span>
                {line}
              </span>
            );
          })}
        </code>
      </pre>
      {data.captionMd && (
        <figcaption className={styles.codeCaption}>
          <MarkdownBlock md={data.captionMd} />
        </figcaption>
      )}
    </figure>
  );
}
