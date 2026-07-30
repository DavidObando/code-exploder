import { create } from 'zustand';

// Ephemeral tutorial UI only — server state (TOC, sections, progress) lives in
// TanStack Query. Diagram stages are keyed by block id so several diagrams on a
// page keep independent steppers.

interface TutorialState {
  stageByBlock: Record<string, number>;
  setStage: (blockId: string, stage: number) => void;
}

export const useTutorial = create<TutorialState>((set) => ({
  stageByBlock: {},
  setStage: (blockId, stage) =>
    set((s) => ({ stageByBlock: { ...s.stageByBlock, [blockId]: stage } })),
}));
