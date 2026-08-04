import type {
  AnalysisSnapshot,
  AppConfig,
  ChunkPeek,
  CreateSessionRequest,
  ExperienceToc,
  Health,
  KbStatus,
  Me,
  QaMessage,
  QaThread,
  Quiz,
  QuizAttempt,
  QuizAttemptRequest,
  SectionDetail,
  SectionProgressResponse,
  SectionUserState,
  SendMessageRequest,
  SendMessageResponse,
  SessionSummary,
  SystemStatus,
} from './types';

/** API errors carry the HTTP status plus the server's `{ message }` when present. */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.body !== undefined) headers.set('Content-Type', 'application/json');
  const res = await fetch(path, { ...init, headers });

  if (!res.ok) {
    let message = `Request failed (${res.status})`;
    try {
      const body = (await res.json()) as { message?: string };
      if (body && typeof body.message === 'string') message = body.message;
    } catch {
      // Non-JSON error body — keep the generic message.
    }
    throw new ApiError(res.status, message);
  }

  if (res.status === 204) return undefined as T;
  // Some 202 responses have no body (e.g. cancel).
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  getConfig: () => request<AppConfig>('/api/config'),
  getMe: () => request<Me>('/api/me'),
  getHealth: () => request<Health>('/healthz'),
  getSystemStatus: () => request<SystemStatus>('/api/system/status'),
  listSessions: () => request<SessionSummary[]>('/api/sessions'),
  createSession: (body: CreateSessionRequest) =>
    request<SessionSummary>('/api/sessions', { method: 'POST', body: JSON.stringify(body) }),
  getSession: (id: string) => request<SessionSummary>(`/api/sessions/${encodeURIComponent(id)}`),
  deleteSession: (id: string) =>
    request<void>(`/api/sessions/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  /** Re-runs a failed session from scratch. 409 (ApiError) when it isn't failed. */
  retrySession: (id: string) =>
    request<SessionSummary>(`/api/sessions/${encodeURIComponent(id)}/retry`, { method: 'POST' }),
  getAnalysis: (id: string) =>
    request<AnalysisSnapshot>(`/api/sessions/${encodeURIComponent(id)}/analysis`),
  /** 404 (ApiError) while the analysis hasn't planned sections yet. */
  getExperience: (sessionId: string) =>
    request<ExperienceToc>(`/api/sessions/${encodeURIComponent(sessionId)}/experience`),
  getSection: (sectionId: string) =>
    request<SectionDetail>(`/api/sections/${encodeURIComponent(sectionId)}`),
  setSectionProgress: (sectionId: string, state: SectionUserState) =>
    request<SectionProgressResponse>(
      `/api/sections/${encodeURIComponent(sectionId)}/progress`,
      { method: 'PUT', body: JSON.stringify({ state }) },
    ),
  getQuiz: (quizId: string) => request<Quiz>(`/api/quizzes/${encodeURIComponent(quizId)}`),
  /** Newest first. */
  listQuizAttempts: (quizId: string) =>
    request<QuizAttempt[]>(`/api/quizzes/${encodeURIComponent(quizId)}/attempts`),
  submitQuizAttempt: (quizId: string, body: QuizAttemptRequest) =>
    request<QuizAttempt>(`/api/quizzes/${encodeURIComponent(quizId)}/attempts`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  getKb: (sessionId: string) =>
    request<KbStatus>(`/api/sessions/${encodeURIComponent(sessionId)}/kb`),
  listThreads: (sessionId: string) =>
    request<QaThread[]>(`/api/sessions/${encodeURIComponent(sessionId)}/threads`),
  createThread: (sessionId: string, body: { title?: string } = {}) =>
    request<QaThread>(`/api/sessions/${encodeURIComponent(sessionId)}/threads`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  listMessages: (threadId: string) =>
    request<QaMessage[]>(`/api/threads/${encodeURIComponent(threadId)}/messages`),
  /** 202; 409 (ApiError) when a generation is already in flight on the thread. */
  sendMessage: (threadId: string, body: SendMessageRequest) =>
    request<SendMessageResponse>(`/api/threads/${encodeURIComponent(threadId)}/messages`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  /** 202; partial text kept, message resolves with status 'cancelled'. */
  cancelMessage: (messageId: string) =>
    request<void>(`/api/messages/${encodeURIComponent(messageId)}/cancel`, { method: 'POST' }),
  getChunk: (analysisId: string, chunkId: string) =>
    request<ChunkPeek>(
      `/api/analyses/${encodeURIComponent(analysisId)}/chunks/${encodeURIComponent(chunkId)}`,
    ),
  /**
   * Starts origin-story generation (repo sessions only). 202; 409 (ApiError)
   * when story sections already exist or are generating; 400 for PR sessions.
   */
  startStory: (sessionId: string) =>
    request<void>(`/api/sessions/${encodeURIComponent(sessionId)}/story`, { method: 'POST' }),
};
