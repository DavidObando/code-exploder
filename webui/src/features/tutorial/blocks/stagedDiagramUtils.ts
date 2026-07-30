import type { DiagramData, DiagramStage } from '../../../api/types';

export interface RevealSet {
  nodes: Set<string>;
  edges: Set<number>;
}

/**
 * Stage k shows the union of the reveal sets of stages 0..k (docs/05-ux.md,
 * progressive whiteboard). Pure — unit-tested without mermaid.
 */
export function computeReveal(stages: DiagramStage[], upToStage: number): RevealSet {
  const nodes = new Set<string>();
  const edges = new Set<number>();
  const last = Math.min(upToStage, stages.length - 1);
  for (let i = 0; i <= last; i++) {
    for (const n of stages[i].reveal.nodes) nodes.add(n);
    for (const e of stages[i].reveal.edges) edges.add(e);
  }
  return { nodes, edges };
}

export interface DiagramMap {
  /** mermaid node/participant id → SVG elements to ghost together. */
  nodes: Map<string, Element[]>;
  /** edge/message index (document order) → SVG elements (path + label). */
  edges: Element[][];
  /** True when every id/index referenced by the stages resolved to elements. */
  complete: boolean;
}

/** Every node id and edge index referenced across all stages. */
function referenced(stages: DiagramStage[]): { nodes: Set<string>; edges: Set<number> } {
  return computeReveal(stages, stages.length - 1);
}

const FLOWCHART_NODE_ID = /^(?:.*?flowchart-)(.+)-\d+$/;

function mapFlowchart(svg: SVGSVGElement): Pick<DiagramMap, 'nodes' | 'edges'> {
  const nodes = new Map<string, Element[]>();
  for (const el of Array.from(svg.querySelectorAll('g.node'))) {
    // mermaid v11 flowchart node ids look like "flowchart-<nodeId>-<n>".
    const match = FLOWCHART_NODE_ID.exec(el.id);
    if (!match) continue;
    const id = match[1];
    nodes.set(id, [...(nodes.get(id) ?? []), el]);
  }
  // Edge paths (and their labels) are emitted in source document order.
  const paths = Array.from(svg.querySelectorAll('.edgePaths path'));
  const labels = Array.from(svg.querySelectorAll('.edgeLabels .edgeLabel'));
  const edges = paths.map((p, i) => (labels[i] ? [p, labels[i]] : [p]));
  return { nodes, edges };
}

function mapSequence(svg: SVGSVGElement): Pick<DiagramMap, 'nodes' | 'edges'> {
  const nodes = new Map<string, Element[]>();
  // Actor boxes carry a name attribute (top and bottom box per participant).
  for (const el of Array.from(svg.querySelectorAll('[name]'))) {
    if (!el.getAttribute('class')?.includes('actor')) continue;
    const name = el.getAttribute('name');
    if (!name) continue;
    nodes.set(name, [...(nodes.get(name) ?? []), el]);
  }
  // Messages: line + text pairs in document (= source) order.
  const lines = Array.from(svg.querySelectorAll('.messageLine0, .messageLine1'));
  const texts = Array.from(svg.querySelectorAll('text.messageText'));
  const edges = lines.map((l, i) => (texts[i] ? [l, texts[i]] : [l]));
  return { nodes, edges };
}

/**
 * Maps the rendered SVG to reveal metadata. When the mermaid DOM shape differs
 * from what we expect, `complete` is false and the caller reveals everything —
 * a fully visible diagram, never a broken one.
 */
export function buildDiagramMap(svg: SVGSVGElement, data: DiagramData): DiagramMap {
  const { nodes, edges } =
    data.diagramKind === 'sequence' ? mapSequence(svg) : mapFlowchart(svg);
  const needed = referenced(data.stages);
  const complete =
    [...needed.nodes].every((id) => (nodes.get(id) ?? []).length > 0) &&
    [...needed.edges].every((i) => i >= 0 && i < edges.length);
  return { nodes, edges, complete };
}

/**
 * Applies ghosting for a stage. `revealAll` (fallback mode or no stages) clears
 * every ghost.
 */
export function applyReveal(map: DiagramMap, reveal: RevealSet, revealAll: boolean) {
  for (const [id, els] of map.nodes) {
    const ghost = !revealAll && !reveal.nodes.has(id);
    for (const el of els) el.classList.toggle('ghost', ghost);
  }
  map.edges.forEach((els, i) => {
    const ghost = !revealAll && !reveal.edges.has(i);
    for (const el of els) el.classList.toggle('ghost', ghost);
  });
}
