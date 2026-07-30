// Hand-written M0 API contract types. OpenAPI codegen replaces this file in a later
// milestone; keep shapes exactly in sync with the backend (docs/04-api.md).

export interface AppConfig {
  authMode: 'DevBypass';
}

export interface Me {
  name: string;
  subject: string;
}

export interface Health {
  status: string;
}

export interface SystemStatus {
  db: boolean;
  queue: {
    depth: number;
    activeJobs: number;
  };
}

export type SessionStatus = 'queued' | 'analyzing' | 'ready' | 'partial' | 'failed';

export interface SessionSummary {
  id: string;
  kind: 'repo' | 'pr';
  title: string;
  repoOwner: string;
  repoName: string;
  prNumber: number | null;
  status: SessionStatus;
  failureReason: string | null;
  createdAt: string;
  progress: {
    completedSections: number;
    totalSections: number;
  };
}

export interface CreateSessionRequest {
  url: string;
  gitRef?: string;
}

export type StageState = 'pending' | 'active' | 'done' | 'failed';

export interface StageInfo {
  key: string;
  label: string;
  state: StageState;
  percent: number | null;
  detail: string | null;
}

export interface AnalysisSnapshot {
  status: SessionStatus;
  stages: StageInfo[];
  narration: { at: string; text: string }[];
  lastEventId: number;
}
