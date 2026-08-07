import type { StageState } from './types';

// SignalR hub envelope (`sessionEvent` on /hubs/session) and per-kind payloads.

export interface SessionEventEnvelope {
  id: number;
  sessionId: string;
  kind: string;
  at: string;
  data: unknown;
}

export interface AnalysisStageChangedData {
  stage: string;
  state: StageState;
}

export interface AnalysisProgressData {
  stage: string;
  percent: number;
  detail?: string | null;
}

export interface AnalysisNarrationData {
  text: string;
}

export interface SectionReadyData {
  sectionId: string;
  slug: string;
  title: string;
  ordinal: number;
  /** Absent on pre-M10 payloads; 0 = main-tour section. */
  depth?: number;
  parentSectionId?: string | null;
}

// --- Deep dives (M10) ---

export interface DeepDivePlannedData {
  explosionId: string;
  componentId: string;
  componentName: string;
  sectionId: string;
  parentSectionId: string | null;
  depth: number;
  trigger: 'eager' | 'on_demand';
}

export interface DeepDiveReadyData {
  explosionId: string;
  componentId: string;
  sectionId: string;
  ready: number;
  total: number;
}

export interface DeepDiveFailedData {
  explosionId: string | null;
  componentId: string | null;
  sectionId: string | null;
  reason: string;
}

export interface AnalysisFailedData {
  reason: string;
}

export interface QuizReadyData {
  sectionId: string;
  quizId: string;
}

export interface QuizGradedData {
  attemptId: string;
  quizId: string;
  scorePct: number;
  sectionId: string;
}

/** Coalesced text deltas — live only, never persisted, absent from catch-up. */
export interface QaTokenData {
  messageId: string;
  seq: number;
  text: string;
}

export interface QaMessageCompletedData {
  messageId: string;
  threadId: string;
  status: 'complete' | 'error' | 'cancelled';
  citations: { path: string; startLine: number; endLine: number; chunkId: string }[] | null;
}
