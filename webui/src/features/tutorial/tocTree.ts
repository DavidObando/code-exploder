import type { SectionTocEntry } from '../../api/types';

// The single source of tree shape and "tour order" (M10). Nav rendering, j/k
// traversal, the index redirect, and the end-card all flow through here — and a
// future audio/video exporter would read flattenAll as its playlist order.

export interface TocNode {
  entry: SectionTocEntry;
  children: TocNode[];
}

/**
 * Groups sections into a tree by parentSectionId, siblings ordered by ord. A
 * child whose parent isn't in the list yet (catch-up race mid-explosion) falls
 * back to the root rather than being dropped; the next invalidation heals it.
 */
export function buildTocTree(sections: SectionTocEntry[]): TocNode[] {
  const sorted = [...sections].sort((a, b) => a.ord - b.ord);
  const byId = new Map<string, TocNode>(sorted.map((s) => [s.id, { entry: s, children: [] }]));
  const roots: TocNode[] = [];
  for (const s of sorted) {
    const node = byId.get(s.id)!;
    const parent = s.parentSectionId ? byId.get(s.parentSectionId) : undefined;
    (parent ? parent.children : roots).push(node);
  }
  return roots;
}

/**
 * Effective collapsed state: deep-dive branches start collapsed (eager dives
 * arrive quietly — the roll-up dot announces them); everything else expanded.
 */
export function isCollapsed(
  entry: SectionTocEntry,
  override: Record<string, boolean>,
): boolean {
  return override[entry.id] ?? entry.kind === 'deep-dive';
}

/** Depth-first flatten of VISIBLE rows (collapsed subtrees skipped). */
export function flattenVisible(
  roots: TocNode[],
  override: Record<string, boolean>,
): SectionTocEntry[] {
  const out: SectionTocEntry[] = [];
  const walk = (nodes: TocNode[]) => {
    for (const node of nodes) {
      out.push(node.entry);
      if (!isCollapsed(node.entry, override)) walk(node.children);
    }
  };
  walk(roots);
  return out;
}

/** Depth-first flatten of EVERYTHING — the canonical linear tour order. */
export function flattenAll(roots: TocNode[]): SectionTocEntry[] {
  const out: SectionTocEntry[] = [];
  const walk = (nodes: TocNode[]) => {
    for (const node of nodes) {
      out.push(node.entry);
      walk(node.children);
    }
  };
  walk(roots);
  return out;
}

/** Any ready-but-never-viewed section below this node (the roll-up dot). */
export function hasUnreadDescendant(node: TocNode): boolean {
  return node.children.some(
    (c) =>
      (c.entry.status === 'ready' && c.entry.myState === 'unread') || hasUnreadDescendant(c),
  );
}

/** Ancestor ids of a section, nearest first (for auto-expanding its branch). */
export function ancestorIds(sections: SectionTocEntry[], id: string): string[] {
  const byId = new Map(sections.map((s) => [s.id, s]));
  const out: string[] = [];
  let current = byId.get(id)?.parentSectionId ?? null;
  while (current) {
    if (out.includes(current)) break; // cycle guard on malformed data
    out.push(current);
    current = byId.get(current)?.parentSectionId ?? null;
  }
  return out;
}

/** Sections inside any deep-dive subtree vs the main tour (header accounting). */
export function partitionMainTour(sections: SectionTocEntry[]): {
  mainTour: SectionTocEntry[];
  deepDive: SectionTocEntry[];
} {
  const inDive = new Set<string>();
  const roots = buildTocTree(sections);
  const mark = (nodes: TocNode[], inside: boolean) => {
    for (const node of nodes) {
      const nowInside = inside || node.entry.kind === 'deep-dive';
      if (nowInside) inDive.add(node.entry.id);
      mark(node.children, nowInside);
    }
  };
  mark(roots, false);
  return {
    mainTour: sections.filter((s) => !inDive.has(s.id)),
    deepDive: sections.filter((s) => inDive.has(s.id)),
  };
}
