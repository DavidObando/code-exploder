import type {
  AnalysisSnapshot,
  AppConfig,
  CreateSessionRequest,
  ExperienceToc,
  Health,
  Me,
  Quiz,
  QuizAttempt,
  QuizAttemptRequest,
  SectionDetail,
  SectionProgressResponse,
  SectionUserState,
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
  return (await res.json()) as T;
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
};
