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
  ordinal: number;
  title: string;
}

export interface AnalysisFailedData {
  reason: string;
}
