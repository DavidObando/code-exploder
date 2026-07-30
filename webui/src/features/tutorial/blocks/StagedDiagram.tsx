import { useCallback, useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import type { DiagramData } from '../../../api/types';
import { useTutorial } from '../../../store/tutorial';
import { useUi } from '../../../store/ui';
import { applyReveal, buildDiagramMap, computeReveal } from './stagedDiagramUtils';
import type { DiagramMap } from './stagedDiagramUtils';
import { MarkdownBlock } from './MarkdownBlock';
import styles from './blocks.module.css';

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function themeVariables() {
  return {
    background: cssVar('--bg-1'),
    primaryColor: cssVar('--bg-2'),
    primaryTextColor: cssVar('--text-primary'),
    primaryBorderColor: cssVar('--border-strong'),
    secondaryColor: cssVar('--bg-2'),
    tertiaryColor: cssVar('--bg-0'),
    lineColor: cssVar('--text-secondary'),
    textColor: cssVar('--text-primary'),
    mainBkg: cssVar('--bg-2'),
    nodeBorder: cssVar('--border-strong'),
    clusterBkg: cssVar('--bg-0'),
    clusterBorder: cssVar('--border'),
    edgeLabelBackground: cssVar('--bg-1'),
    actorBkg: cssVar('--bg-2'),
    actorBorder: cssVar('--border-strong'),
    actorTextColor: cssVar('--text-primary'),
    signalColor: cssVar('--text-secondary'),
    signalTextColor: cssVar('--text-secondary'),
    noteBkgColor: cssVar('--bg-2'),
    noteTextColor: cssVar('--text-primary'),
    noteBorderColor: cssVar('--border'),
    fontFamily: cssVar('--font-ui') || 'Inter, system-ui, sans-serif',
  };
}

function isEditableTarget(target: EventTarget | null): boolean {
  return (
    target instanceof HTMLElement &&
    (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)
  );
}

let renderCounter = 0;

/**
 * The progressive whiteboard (docs/05-ux.md): the FULL mermaid document renders
 * once; stage k reveals the union of stages 0..k, everything else is ghosted by
 * CSS. If element mapping fails, everything is revealed (console.warn) — a fully
 * visible diagram, never a broken one. `deepLink` marks the section's first
 * diagram: it owns ?stage=n and the [ / ] shortcuts.
 */
export function StagedDiagram({
  data,
  blockId,
  deepLink = false,
}: {
  data: DiagramData;
  blockId: string;
  deepLink?: boolean;
}) {
  const theme = useUi((s) => s.theme);
  const [searchParams, setSearchParams] = useSearchParams();
  const stageCount = data.stages.length;
  const maxStage = Math.max(0, stageCount - 1);

  const storedStage = useTutorial((s) => s.stageByBlock[blockId]);
  const setStoredStage = useTutorial((s) => s.setStage);

  // ?stage=n is 1-based; it seeds the store once for the deep-linkable diagram.
  const seededRef = useRef(false);
  useEffect(() => {
    if (!deepLink || seededRef.current) return;
    seededRef.current = true;
    const raw = Number(searchParams.get('stage'));
    if (Number.isInteger(raw) && raw >= 1) {
      setStoredStage(blockId, Math.min(raw - 1, maxStage));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [deepLink, blockId]);

  const stage = Math.min(Math.max(storedStage ?? 0, 0), maxStage);

  const setStage = useCallback(
    (next: number) => {
      const clamped = Math.min(Math.max(next, 0), maxStage);
      setStoredStage(blockId, clamped);
      if (deepLink) {
        setSearchParams(
          (prev) => {
            const params = new URLSearchParams(prev);
            params.set('stage', String(clamped + 1));
            return params;
          },
          { replace: true },
        );
      }
    },
    [blockId, deepLink, maxStage, setSearchParams, setStoredStage],
  );

  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<DiagramMap | null>(null);
  const warnedRef = useRef(false);
  const [renderState, setRenderState] = useState<'loading' | 'ok' | 'failed'>('loading');

  const applyStage = useCallback(
    (stageIndex: number) => {
      const map = mapRef.current;
      if (!map || stageCount === 0) return;
      if (!map.complete && !warnedRef.current) {
        warnedRef.current = true;
        console.warn(
          `StagedDiagram: could not map reveal metadata onto the rendered SVG for "${data.title}" — revealing everything.`,
        );
      }
      applyReveal(map, computeReveal(data.stages, stageIndex), !map.complete);
    },
    [data.stages, data.title, stageCount],
  );

  // Render the full document once per (source, theme). mermaid is lazy-loaded so
  // its chunk is only fetched when a diagram block is actually on screen.
  useEffect(() => {
    let cancelled = false;
    setRenderState('loading');
    (async () => {
      try {
        const mermaid = (await import('mermaid')).default;
        mermaid.initialize({
          startOnLoad: false,
          securityLevel: 'strict',
          theme: 'base',
          themeVariables: themeVariables(),
        });
        const { svg } = await mermaid.render(`staged-diagram-${++renderCounter}`, data.mermaid);
        if (cancelled) return;
        const container = containerRef.current;
        if (!container) return;
        container.innerHTML = svg;
        const svgEl = container.querySelector('svg');
        mapRef.current = svgEl ? buildDiagramMap(svgEl, data) : null;
        setRenderState('ok');
      } catch (err) {
        if (cancelled) return;
        console.warn(`StagedDiagram: mermaid failed to render "${data.title}"`, err);
        setRenderState('failed');
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data.mermaid, theme]);

  // Ghosting is pure class toggling — no re-render, layout never jumps.
  useEffect(() => {
    if (renderState === 'ok') applyStage(stage);
  }, [renderState, stage, applyStage]);

  // [ / ] step the deep-linkable diagram's stages.
  useEffect(() => {
    if (!deepLink || stageCount === 0) return;
    const onKey = (e: KeyboardEvent) => {
      if (isEditableTarget(e.target)) return;
      if (e.key === '[') setStage(stage - 1);
      else if (e.key === ']') setStage(stage + 1);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [deepLink, stage, stageCount, setStage]);

  // Degraded mode (docs/05): numbered narration instead of a broken diagram.
  if (renderState === 'failed') {
    return (
      <section className={styles.diagram} aria-label={data.title}>
        <h3 className={styles.diagramTitle}>{data.title}</h3>
        <div className={styles.diagramFallback}>
          <ol>
            {data.stages.map((s, i) => (
              <li key={i}>
                <strong>{s.title}.</strong>
                <MarkdownBlock md={s.narrationMd} />
              </li>
            ))}
          </ol>
        </div>
      </section>
    );
  }

  const current = stageCount > 0 ? data.stages[stage] : null;

  return (
    <section className={styles.diagram} aria-label={data.title}>
      <h3 className={styles.diagramTitle}>{data.title}</h3>
      {renderState === 'loading' && <div className={styles.diagramLoading}>Sketching…</div>}
      <div className={styles.diagramCanvas} ref={containerRef} />
      {stageCount > 0 && (
        <>
          <div className={styles.stepper}>
            <button
              className={styles.stepButton}
              onClick={() => setStage(stage - 1)}
              disabled={stage === 0}
              aria-label="Previous stage"
            >
              ◀
            </button>
            <span className={styles.stepperLabel}>
              Stage {stage + 1} of {stageCount}
            </span>
            <button
              className={styles.stepButton}
              onClick={() => setStage(stage + 1)}
              disabled={stage === maxStage}
              aria-label="Next stage"
            >
              ▶
            </button>
            {deepLink && <span className={styles.stepperKeys}>[ and ] step stages</span>}
          </div>
          {current && (
            <div className={styles.narration} key={stage}>
              <div className={styles.narrationTitle}>{current.title}</div>
              <MarkdownBlock md={current.narrationMd} />
            </div>
          )}
        </>
      )}
    </section>
  );
}
