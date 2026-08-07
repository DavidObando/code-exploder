import { create } from 'zustand';

// Ephemeral tutorial UI only — server state (TOC, sections, progress) lives in
// TanStack Query. Diagram stages are keyed by block id so several diagrams on a
// page keep independent steppers.

interface TutorialState {
  stageByBlock: Record<string, number>;
  setStage: (blockId: string, stage: number) => void;
  /** Quizzes the learner dismissed with "Skip quiz" — UI-only, this session. */
  skippedQuizzes: Record<string, boolean>;
  setQuizSkipped: (quizId: string, skipped: boolean) => void;
  /**
   * Explicit expand/collapse overrides by section id (M10). Absent = default:
   * deep-dive branches collapsed, everything else expanded (see tocTree).
   * Deliberately not persisted — the ancestors-of-current auto-expand rebuilds
   * correct expansion on reload/deep-link.
   */
  collapsedOverride: Record<string, boolean>;
  setCollapsed: (sectionId: string, collapsed: boolean) => void;
  expandAll: (sectionIds: string[]) => void;
  /** Scope componentIds whose explosion THIS tab initiated — gates toasts. */
  pendingExplosions: Record<string, true>;
  markExplosionPending: (componentId: string) => void;
  clearExplosionPending: (componentId: string) => void;
}

export const useTutorial = create<TutorialState>((set) => ({
  stageByBlock: {},
  setStage: (blockId, stage) =>
    set((s) => ({ stageByBlock: { ...s.stageByBlock, [blockId]: stage } })),
  skippedQuizzes: {},
  setQuizSkipped: (quizId, skipped) =>
    set((s) => ({ skippedQuizzes: { ...s.skippedQuizzes, [quizId]: skipped } })),
  collapsedOverride: {},
  setCollapsed: (sectionId, collapsed) =>
    set((s) => ({ collapsedOverride: { ...s.collapsedOverride, [sectionId]: collapsed } })),
  expandAll: (sectionIds) =>
    set((s) => {
      if (sectionIds.every((id) => s.collapsedOverride[id] === false)) return s;
      const next = { ...s.collapsedOverride };
      for (const id of sectionIds) next[id] = false;
      return { collapsedOverride: next };
    }),
  pendingExplosions: {},
  markExplosionPending: (componentId) =>
    set((s) => ({ pendingExplosions: { ...s.pendingExplosions, [componentId]: true } })),
  clearExplosionPending: (componentId) =>
    set((s) => {
      if (!s.pendingExplosions[componentId]) return s;
      const next = { ...s.pendingExplosions };
      delete next[componentId];
      return { pendingExplosions: next };
    }),
}));
