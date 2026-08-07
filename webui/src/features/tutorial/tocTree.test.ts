import type { SectionTocEntry } from '../../api/types';
import {
  ancestorIds,
  buildTocTree,
  flattenAll,
  flattenVisible,
  hasUnreadDescendant,
  isCollapsed,
  partitionMainTour,
} from './tocTree';

function entry(overrides: Partial<SectionTocEntry>): SectionTocEntry {
  return {
    id: 'id',
    slug: 'slug',
    kind: 'architecture',
    title: 'Untitled',
    summary: '',
    ord: 0,
    depth: 0,
    parentSectionId: null,
    estimatedMinutes: 5,
    status: 'ready',
    myState: 'unread',
    hasQuiz: false,
    quizBestPct: null,
    componentId: null,
    ...overrides,
  };
}

// intro(0) · arch(1) ─┬─ dd(10, deep-dive) ─┬─ dd-tour(11)
//                     │                     └─ dd-flow(12)
// build(2)            └ (dd anchored under arch)
const sections: SectionTocEntry[] = [
  entry({ id: 'intro', slug: 'intro', ord: 0 }),
  entry({ id: 'arch', slug: 'arch', ord: 1 }),
  entry({ id: 'build', slug: 'build', ord: 2 }),
  entry({ id: 'dd', slug: 'dd-core', kind: 'deep-dive', ord: 10, depth: 1, parentSectionId: 'arch' }),
  entry({ id: 'dd-tour', slug: 'dd-core-tour', kind: 'deep-dive-tour', ord: 11, depth: 2, parentSectionId: 'dd' }),
  entry({ id: 'dd-flow', slug: 'dd-core-flow', kind: 'deep-dive-flow', ord: 12, depth: 2, parentSectionId: 'dd' }),
];

describe('buildTocTree', () => {
  it('nests children under parents, siblings by ord', () => {
    const roots = buildTocTree(sections);
    expect(roots.map((r) => r.entry.id)).toEqual(['intro', 'arch', 'build']);
    const arch = roots[1];
    expect(arch.children.map((c) => c.entry.id)).toEqual(['dd']);
    expect(arch.children[0].children.map((c) => c.entry.id)).toEqual(['dd-tour', 'dd-flow']);
  });

  it('orphans fall back to the root instead of vanishing', () => {
    const orphaned = [entry({ id: 'a', ord: 0 }), entry({ id: 'b', ord: 1, parentSectionId: 'ghost' })];
    const roots = buildTocTree(orphaned);
    expect(roots.map((r) => r.entry.id)).toEqual(['a', 'b']);
  });
});

describe('collapse defaults and flattening', () => {
  it('deep-dive branches start collapsed; overrides win both ways', () => {
    const dd = sections.find((s) => s.id === 'dd')!;
    expect(isCollapsed(dd, {})).toBe(true);
    expect(isCollapsed(dd, { dd: false })).toBe(false);
    expect(isCollapsed(sections[1], {})).toBe(false);
    expect(isCollapsed(sections[1], { arch: true })).toBe(true);
  });

  it('flattenVisible skips collapsed subtrees; flattenAll never does', () => {
    const roots = buildTocTree(sections);
    expect(flattenVisible(roots, {}).map((s) => s.id)).toEqual(['intro', 'arch', 'dd', 'build']);
    expect(flattenVisible(roots, { dd: false }).map((s) => s.id)).toEqual([
      'intro', 'arch', 'dd', 'dd-tour', 'dd-flow', 'build',
    ]);
    expect(flattenAll(roots).map((s) => s.id)).toEqual([
      'intro', 'arch', 'dd', 'dd-tour', 'dd-flow', 'build',
    ]);
  });

  it('tree order beats raw ord order (dive children before later top-level ords)', () => {
    // dd children have ords 11-12, far above build's 2 — tree order interleaves.
    const all = flattenAll(buildTocTree(sections)).map((s) => s.id);
    expect(all.indexOf('dd-flow')).toBeLessThan(all.indexOf('build'));
  });
});

describe('hasUnreadDescendant', () => {
  it('sees ready+unread anywhere below, ignoring non-ready and viewed rows', () => {
    const roots = buildTocTree(sections);
    const arch = roots[1];
    expect(hasUnreadDescendant(arch)).toBe(true);

    const viewed = sections.map((s) =>
      s.depth > 0 ? { ...s, myState: 'read' as const } : s,
    );
    const viewedArch = buildTocTree(viewed)[1];
    expect(hasUnreadDescendant(viewedArch)).toBe(false);

    const generating = sections.map((s) =>
      s.depth > 0 ? { ...s, status: 'generating' as const } : s,
    );
    expect(hasUnreadDescendant(buildTocTree(generating)[1])).toBe(false);
  });
});

describe('ancestorIds', () => {
  it('walks nearest-first to the root', () => {
    expect(ancestorIds(sections, 'dd-flow')).toEqual(['dd', 'arch']);
    expect(ancestorIds(sections, 'intro')).toEqual([]);
  });

  it('survives a cycle in malformed data', () => {
    const cyclic = [
      entry({ id: 'a', parentSectionId: 'b' }),
      entry({ id: 'b', parentSectionId: 'a' }),
    ];
    expect(ancestorIds(cyclic, 'a')).toEqual(['b', 'a']);
  });
});

describe('partitionMainTour', () => {
  it('splits dive subtrees (including the dd parent) from the main tour', () => {
    const { mainTour, deepDive } = partitionMainTour(sections);
    expect(mainTour.map((s) => s.id)).toEqual(['intro', 'arch', 'build']);
    expect(deepDive.map((s) => s.id).sort()).toEqual(['dd', 'dd-flow', 'dd-tour']);
  });
});
