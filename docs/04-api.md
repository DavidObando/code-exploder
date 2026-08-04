# 04 — API surface and SignalR contract

## Conventions

- Minimal APIs grouped into static `{Session,Analysis,Experience,Progress,Quiz,Chat,
  System}Endpoints.Map(app)` classes; every route chains `.RequireAuthorization()` and
  `.Produces<T>()`.
- OpenAPI emitted at build time (`--emit-openapi` run mode) into a checked-in
  `webui/openapi.json`; the SPA generates a typed client from it
  (`openapi-typescript` + `openapi-fetch`).
- **Auth** (`Auth:Mode`): `DevBypass` (synthetic user; refuses to start outside
  Development) · `SharedGate` (production default: reverse-proxy forward-auth trusts
  the forwarded user header, or a shared-credential cookie login; either resolves to a
  stable `subject`) · `Oidc` (JWT bearer; WebSocket auth via `?access_token=` on hub
  paths). Single role in v1.
- **All session-scoped routes verify `session.user_id == current user` and return 404
  otherwise** — no existence leaks.
- Errors: 4xx with `ErrorResponse { message }`; enums serialized as strings.

## Endpoints

### Sessions
| Route | Verb | Request | Response |
|---|---|---|---|
| `/api/sessions` | GET | — | `SessionSummary[]` — `{id, kind, title, repoOwner, repoName, prNumber?, status, failureReason?, createdAt, progress: {completedSections, totalSections}, queuePosition?}` |
| `/api/sessions` | POST | `{url, gitRef?}` — server parses repo vs PR URL; 400 on unparseable/non-GitHub/private | `201 SessionSummary` (status `queued`) |
| `/api/sessions/{id}` | GET | — | summary + `{commitSha, experienceVersion, analyzedAt}` |
| `/api/sessions/{id}` | DELETE | — | `204` (cascades content, chunks, threads) |
| `/api/sessions/{id}/retry` | POST | — | `200 SessionSummary` — failed → queued on a fresh analysis (same gitRef); `409` when not failed |
| `/api/sessions/{id}/refresh` | POST | — | `202` — re-analyze at latest ref → experience v(n+1); progress carries over by slug |
| `/api/sessions/{id}/cancel` | POST | — | `202` |

### Analysis progress
| Route | Verb | Response |
|---|---|---|
| `/api/sessions/{id}/analysis` | GET | `{status, stages: [{key, label, state, percent?, detail?, elapsedMs?}], narration: [{at, text}], lastEventId}` — mount snapshot; deltas via hub, `lastEventId` seeds catch-up |

### Experience content
| Route | Verb | Response |
|---|---|---|
| `/api/sessions/{id}/experience` | GET | TOC + per-user progress: `{experienceId, version, commitSha, repoSummary, sections: [{id, slug, kind, title, summary, depth, parentSectionId?, ord, estimatedMinutes, status, hasQuiz, myState}]}` — available while `partial` |
| `/api/sections/{sectionId}` | GET | `{id, slug, title, kind, blocks: Block[]}` — typed union on `type`; quiz blocks carry only `quizId` |
| `/api/sections/{sectionId}/deep-dives` | POST | `{topic?}` → `202 {jobId}` for on-demand dives; `200 {sectionId}` if cached |
| `/api/analyses/{id}/chunks/{chunkId}` | GET | source viewer payload for citations |

### Progress
| Route | Verb | Request → Response |
|---|---|---|
| `/api/sections/{sectionId}/progress` | PUT | `{state: 'read'\|'skipped'\|'completed'\|'unread'}` → `{sectionId, state, sessionProgress}` |

### Quizzes
| Route | Verb | Request → Response |
|---|---|---|
| `/api/quizzes/{quizId}` | GET | questions **without answer keys/rubrics** |
| `/api/quizzes/{quizId}/attempts` | POST | `{answers: [{questionId, choiceKeys?, text?}]}` → `201 QuizAttempt`; deterministic parts graded inline; a short answer leaves `status:'grading'` + enqueues `grade-quiz` → `QuizGraded` event |
| `/api/quizzes/{quizId}/attempts` | GET | attempt history with per-question feedback + reread targets |

### Chat
| Route | Verb | Request → Response |
|---|---|---|
| `/api/sessions/{id}/threads` | GET/POST | thread list / `201 Thread` |
| `/api/threads/{threadId}/messages` | GET | messages (a `streaming` row returns its partial content — reconnect story) |
| `/api/threads/{threadId}/messages` | POST | `{content, sectionContext?}` → `202 {userMessageId, assistantMessageId}`; 409 if a generation is in flight on the thread; tokens arrive via hub |
| `/api/messages/{messageId}/cancel` | POST | `202` (keeps partial text, status `cancelled`) |

### System
| Route | Verb | Auth | Response |
|---|---|---|---|
| `/healthz` | GET | anon | `{status}` (db check) |
| `/api/config` | GET | anon | `{authMode, oidc?}` — SPA bootstrap |
| `/api/me` | GET | auth | `{name, subject}` |
| `/api/system/status` | GET | auth | `{db, ollama: {reachable, models}, queue: {depth, activeJobs}, workers: [{name, lastHeartbeat}]}` — feeds the StatusBar |

## SignalR

**Decision: SignalR for everything live, including Q&A token streams — no SSE.** The
app already requires SignalR for pipeline progress; SSE would add a second transport
with its own auth, reconnect, and proxy-buffering concerns. One hub, one reconnect
story.

**Hub**: `SessionHub` at `/hubs/session` (authorized; `?access_token=` for WS).
**Groups**: `session:{sessionId}` (ownership verified in `SubscribeSession`) and
`user:{subject}` (auto-joined on connect — powers the live left-pane session list).

**Envelope** (single client method `sessionEvent`):
```json
{ "id": 4211, "sessionId": "…", "kind": "AnalysisProgress", "at": "…", "data": { } }
```

| Kind | data | Persisted? | Client behavior |
|---|---|---|---|
| `AnalysisStageChanged` | `{stage, state, elapsedMs?}` | yes | invalidate `['analysis',id]`, `['sessions']` |
| `AnalysisProgress` | `{stage, percent, detail?}` | throttled ~1/s | direct render (stage bar) |
| `AnalysisNarration` | `{text}` | yes | append to ticker |
| `SectionReady` | `{sectionId, slug, title, ord, kind}` | yes | invalidate `['experience',id]`; early-entry banner |
| `AnalysisCompleted` | `{experienceId, version}` | yes | invalidate; auto-navigate |
| `AnalysisFailed` | `{reason}` | yes | invalidate; sticky error toast |
| `DeepDiveReady` | `{parentSectionId, sectionId, slug}` | yes | invalidate; flip chip |
| `QaToken` | `{messageId, seq, token}` | **no** | append to streaming message DOM |
| `QaMessageCompleted` | `{messageId, citations}` | yes | invalidate `['thread',threadId]` (swap buffer for canonical row) |
| `QuizGraded` | `{attemptId, quizId, scorePct}` | yes | invalidate attempts + experience |

**Client rule** (ported verbatim from the reference app's live-events service):
*lifecycle events invalidate TanStack Query keys — the server stays authoritative;
stream events (`QaToken`, `AnalysisProgress` percent) render directly and are never a
source of durable state.*

**Reconnect / catch-up**, two tiers:
1. Durable events land in `pipeline_events`; the hub exposes
   `GetEventsSince(sessionId, sinceId)` (limit 500). The client singleton tracks
   `lastEventId`, re-joins groups on reconnect (membership is per-connection), replays
   the gap, and falls back to invalidate-everything if the gap overflows.
2. Q&A tokens are ephemeral: an in-memory ring buffer per active generation +
   `GetAnswerSince(messageId, sinceSeq)` covers small gaps; the message row's ~1/s
   partial-content flush covers server restarts (resolve via persisted partial +
   `status`).
