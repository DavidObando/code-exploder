import { Link } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import type { ExperienceToc, ScopeInfo, SectionTocEntry } from '../../api/types';
import { useTutorial } from '../../store/tutorial';
import { useExplodeScope } from './useExplodeScope';
import { buildTocTree, flattenAll } from './tocTree';
import ui from '../../components/ui.module.css';
import styles from './tutorial.module.css';

/**
 * The "go deeper" affordance (M10): rendered at the end of the architecture
 * section (top-level scopes) and of every deep-dive section (its sub-scopes).
 * Each scope shows its explosion state: explode → generating → continue → retry.
 */
export function GoDeeperCard({
  toc,
  sessionId,
  entry,
}: {
  toc: ExperienceToc;
  sessionId: string;
  entry: SectionTocEntry;
}) {
  const parentComponentId =
    entry.kind === 'deep-dive' ? (entry.componentId ?? undefined) : undefined;
  const scopes = useQuery({
    queryKey: ['scopes', sessionId, parentComponentId ?? 'root'],
    queryFn: () => api.getScopes(sessionId, parentComponentId),
  });

  const list = (scopes.data?.scopes ?? []).filter((s) => s.explodable || s.explosion !== null);
  if (list.length === 0) return null;

  return (
    <div className={styles.goDeeper}>
      <p className={styles.goDeeperTitle}>Want to go deeper?</p>
      <p className={styles.goDeeperHint}>
        Explode a sub-system to generate a guided deep dive into it — new sections join the
        contents tree as they're written.
      </p>
      {list.map((scope) => (
        <ScopeRow key={scope.componentId} scope={scope} sessionId={sessionId} toc={toc} />
      ))}
    </div>
  );
}

function ScopeRow({
  scope,
  sessionId,
  toc,
}: {
  scope: ScopeInfo;
  sessionId: string;
  toc: ExperienceToc;
}) {
  const explode = useExplodeScope(sessionId);
  const setCollapsed = useTutorial((s) => s.setCollapsed);
  const explosion = scope.explosion;

  // "Continue" targets the first ready section of the dive's subtree, tree order.
  const continueSlug = (() => {
    if (!explosion?.sectionId) return null;
    const roots = buildTocTree(toc.sections);
    const subtreeRoot = (function find(nodes: typeof roots): (typeof roots)[number] | null {
      for (const node of nodes) {
        if (node.entry.id === explosion.sectionId) return node;
        const hit = find(node.children);
        if (hit) return hit;
      }
      return null;
    })(roots);
    if (!subtreeRoot) return null;
    return flattenAll([subtreeRoot]).find((s) => s.status === 'ready')?.slug ?? null;
  })();

  const start = () => {
    if (explosion?.sectionId) {
      // Reveal the incoming ◐ rows under the dive as soon as the user acts.
      setCollapsed(explosion.sectionId, false);
    }
    explode.mutate(scope.componentId);
  };

  let action: React.ReactNode;
  if (explosion === null || explosion === undefined) {
    action = (
      <button
        type="button"
        className={ui.buttonPrimary}
        onClick={start}
        disabled={explode.isPending || !scope.explodable}
      >
        ⊕ Explode
      </button>
    );
  } else if (explosion.status === 'queued' || explosion.status === 'running') {
    action = (
      <span className={styles.goDeeperGeneratingNote} role="status">
        ◐ the expert is dissecting {scope.name}…
      </span>
    );
  } else if (explosion.status === 'failed') {
    action = (
      <button
        type="button"
        className={ui.button}
        onClick={start}
        disabled={explode.isPending}
      >
        ↻ Retry
      </button>
    );
  } else if (continueSlug) {
    action = (
      <Link to={`/sessions/${sessionId}/learn/${continueSlug}`} className={ui.button}>
        Continue into the deep dive →
      </Link>
    );
  } else {
    action = <span className={styles.goDeeperGeneratingNote}>ready</span>;
  }

  return (
    <div className={styles.goDeeperRow}>
      <div className={styles.goDeeperScope}>
        <span className={styles.goDeeperName}>{scope.name}</span>
        <span className={styles.goDeeperFiles}>
          {scope.fileCount} files
          {explosion && explosion.sectionsTotal > 0 && explosion.status !== 'ready'
            ? ` · ${explosion.sectionsReady}/${explosion.sectionsTotal} sections`
            : ''}
        </span>
      </div>
      {action}
    </div>
  );
}
