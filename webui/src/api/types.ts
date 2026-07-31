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
  analysisId: string;
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

export interface RepoSummary {
  commitSha: string;
  description: string | null;
  fileCount: number;
  analyzedFileCount: number;
  excludedFileCount: number;
  chunkCount: number;
  totalBytes: number;
  languages: { name: string; files: number; bytes: number; percent: number }[];
  buildSystems: string[];
  ciConfigs: string[];
  entryPoints: string[];
  components: { name: string; fileCount: number; topFiles: string[] }[];
  topChurnFiles: { path: string; commits: number }[];
  commitCount: number;
  contributorCount: number;
}

export interface AnalysisSnapshot {
  status: SessionStatus;
  stages: StageInfo[];
  narration: { at: string; text: string }[];
  lastEventId: number;
  /** Null until the run is ready. */
  summary: RepoSummary | null;
}

// --- Tutorial experience (M2) ---

export type SectionKind =
  | 'intro'
  | 'architecture'
  | 'scenario'
  | 'build'
  | 'pr-overview'
  | 'pr-walkthrough'
  | 'pr-risk'
  | 'story';
export type SectionStatus = 'pending' | 'generating' | 'ready' | 'failed';
export type SectionUserState = 'unread' | 'read' | 'skipped' | 'completed';

export interface SectionTocEntry {
  id: string;
  slug: string;
  kind: SectionKind;
  title: string;
  summary: string;
  ord: number;
  depth: number;
  parentSectionId: string | null;
  estimatedMinutes: number;
  status: SectionStatus;
  myState: SectionUserState;
  hasQuiz: boolean;
  /** Best score across retakes; null when never attempted. */
  quizBestPct: number | null;
}

export interface ExperienceToc {
  experienceId: string;
  version: number;
  commitSha: string;
  model: string;
  generatedAt: string;
  sections: SectionTocEntry[];
}

export interface DiagramStage {
  title: string;
  narrationMd: string;
  reveal: { nodes: string[]; edges: number[] };
}

export interface DiagramData {
  /**
   * 'timeline' (story sections, M9): stages carry narration per era but the
   * reveal sets are empty — no ghosting, the stepper is a narration walker.
   */
  diagramKind: 'flowchart' | 'sequence' | 'timeline';
  title: string;
  /** Full mermaid document — rendered once; stages reveal via CSS ghosting. */
  mermaid: string;
  stages: DiagramStage[];
}

export interface CodeBlockData {
  path: string;
  startLine: number;
  endLine: number;
  language: string;
  content: string;
  captionMd: string | null;
}

export interface CalloutData {
  variant: 'insight' | 'warning' | 'convention';
  title: string;
  md: string;
}

interface BlockBase {
  id: string;
  ord: number;
}

export type Block =
  | (BlockBase & { type: 'markdown'; data: { md: string } })
  | (BlockBase & { type: 'code'; data: CodeBlockData })
  | (BlockBase & { type: 'callout'; data: CalloutData })
  | (BlockBase & { type: 'diagram'; data: DiagramData });

export interface SectionDetail {
  id: string;
  slug: string;
  title: string;
  kind: SectionKind;
  status: SectionStatus;
  blocks: Block[];
  /** Null until the quiz generates — quizzes arrive after their section. */
  quizId: string | null;
}

export interface SectionProgressResponse {
  sectionId: string;
  state: SectionUserState;
  sessionProgress: { completedSections: number; totalSections: number };
}

// --- Quizzes (M3) --- answer keys and rubrics never reach the client.

interface QuizQuestionBase {
  id: string;
  ord: number;
  prompt: string;
}

export type QuizQuestion =
  | (QuizQuestionBase & { type: 'single' | 'multi'; choices: { key: string; text: string }[] })
  | (QuizQuestionBase & { type: 'boolean' })
  | (QuizQuestionBase & { type: 'short'; maxWords: number });

export interface Quiz {
  id: string;
  sectionId: string;
  title: string;
  questions: QuizQuestion[];
}

export interface QuizAnswer {
  questionId: string;
  choiceKeys?: string[];
  text?: string;
}

export interface QuizAttemptRequest {
  answers: QuizAnswer[];
}

export interface QuizAttempt {
  id: string;
  submittedAt: string;
  /** 'grading' only while a short answer awaits the LLM. */
  status: 'grading' | 'graded';
  /** Null while grading. */
  scorePct: number | null;
  perQuestion: {
    questionId: string;
    /** Null = short answer pending or excluded. */
    correct: boolean | null;
    /** Short answer was ungradable — not counted toward the score. */
    excluded: boolean;
    feedbackMd: string | null;
  }[];
}

// --- Q&A virtual expert (M4) ---

export interface KbStatus {
  embeddedChunks: number;
  totalChunks: number;
  ready: boolean;
}

export interface QaThread {
  id: string;
  title: string;
  createdAt: string;
  lastMessageAt: string | null;
}

export interface QaCitation {
  path: string;
  startLine: number;
  endLine: number;
  chunkId: string;
}

export interface QaMessage {
  id: string;
  ord: number;
  role: 'user' | 'assistant';
  /** Partial while status === 'streaming' (flushed ~1/s server-side). */
  content: string;
  status: 'streaming' | 'complete' | 'error' | 'cancelled';
  citations: QaCitation[] | null;
  createdAt: string;
}

export interface SendMessageRequest {
  content: string;
  sectionContext?: string;
}

export interface SendMessageResponse {
  userMessageId: string;
  assistantMessageId: string;
}

export interface ChunkPeek {
  path: string;
  startLine: number;
  endLine: number;
  language: string;
  content: string;
}
