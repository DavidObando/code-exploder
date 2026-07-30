import type { CodeBlockData } from '../../../api/types';
import { MarkdownBlock } from './MarkdownBlock';
import styles from './blocks.module.css';

export interface GithubContext {
  owner: string;
  repo: string;
  commitSha: string;
}

/**
 * Code excerpt: monospace block, line numbers from startLine, SHA-pinned
 * "View on GitHub" deep link. No client-side highlighting in M2 (kept the
 * bundle lean); shiki can slot in here later.
 */
export function CodeExcerpt({ data, github }: { data: CodeBlockData; github: GithubContext | null }) {
  const lines = data.content.replace(/\n$/, '').split('\n');
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
          {lines.map((line, i) => (
            <span key={i} className={styles.codeLine}>
              <span className={styles.lineNo} aria-hidden="true">
                {data.startLine + i}
              </span>
              {line}
            </span>
          ))}
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
