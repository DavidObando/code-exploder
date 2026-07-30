import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import type { QaCitation } from '../../api/types';
import type { GithubContext } from '../tutorial/blocks/CodeExcerpt';
import styles from './chat.module.css';

/**
 * Citation card: monospace path + line range, citation-gold accent; click
 * expands an inline peek of the snapshotted chunk; ↗ opens the SHA-pinned
 * GitHub deep link.
 */
export function CitationCard({
  citation,
  index,
  messageId,
  analysisId,
  github,
}: {
  citation: QaCitation;
  index: number;
  messageId: string;
  analysisId: string;
  github: GithubContext | null;
}) {
  const [expanded, setExpanded] = useState(false);

  const peek = useQuery({
    queryKey: ['chunk', analysisId, citation.chunkId],
    queryFn: () => api.getChunk(analysisId, citation.chunkId),
    enabled: expanded,
    staleTime: Infinity, // snapshotted content never changes
  });

  const href = github
    ? `https://github.com/${github.owner}/${github.repo}/blob/${github.commitSha}/${citation.path}#L${citation.startLine}-L${citation.endLine}`
    : null;

  const lines = peek.data ? peek.data.content.replace(/\n$/, '').split('\n') : [];

  return (
    <div className={styles.citationCard} id={`cite-${messageId}-${index + 1}`}>
      <button
        className={styles.citationHeader}
        onClick={() => setExpanded((v) => !v)}
        aria-expanded={expanded}
      >
        <span className={styles.citationMarker}>[S{index + 1}]</span>
        <span className={styles.citationPath} title={citation.path}>
          {citation.path}
        </span>
        <span className={styles.citationLines}>
          L{citation.startLine}–{citation.endLine}
        </span>
        {href && (
          <a
            className={styles.citationGithub}
            href={href}
            target="_blank"
            rel="noreferrer"
            onClick={(e) => e.stopPropagation()}
            aria-label="View on GitHub"
          >
            ↗
          </a>
        )}
      </button>
      {expanded && peek.isPending && <div className={styles.peekNote}>Fetching snippet…</div>}
      {expanded && peek.isError && (
        <div className={styles.peekNote}>Could not load this snippet.</div>
      )}
      {expanded && peek.data && (
        <pre className={styles.peek} data-language={peek.data.language}>
          <code>
            {lines.map((line, i) => (
              <span key={i} className={styles.peekLine}>
                <span className={styles.peekLineNo} aria-hidden="true">
                  {peek.data.startLine + i}
                </span>
                {line}
              </span>
            ))}
          </code>
        </pre>
      )}
    </div>
  );
}
