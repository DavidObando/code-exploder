# 03 — Data model

PostgreSQL 17 + pgvector. No ORM — raw SQL with hand-written mapping, applied by an
embedded-SQL migration runner (`Migrations/V####__name.sql` as embedded resources,
tracked in `schema_migrations`, serialized across processes by a Postgres advisory
lock so Gateway and all workers can safely migrate at startup).

Two halves: the **analysis side** (what the pipeline produces — the knowledge base)
and the **experience side** (what users consume — sessions, tutorial content,
progress, chat).

## Analysis side

```
repos             id pk, provider, owner, name, url, default_branch, meta jsonb, created_at

analyses          id pk, repo_id fk, kind ('repo'|'pr'), commit_sha, pr_number,
                  base_analysis_id fk null,           -- PR mode: the base repo analysis
                  model, prompt_bundle_version,
                  status ('planning'|'running'|'ready'|'ready_with_warnings'|'failed'|'cancelled'),
                  cancel_requested bool, plan jsonb, stats jsonb, error,
                  created_at, finished_at

jobs              (base queue schema + analysis_id, unblocks_job_id, blocked_count;
                   statuses + 'blocked','cancelled'; partial index on dequeue predicate)

files             id pk, analysis_id fk, path, language, size_bytes, sha256,
                  role jsonb (entrypoint/test/ci/manifest/doc/excluded:reason),
                  churn int, last_touched

chunks            id pk, analysis_id fk, file_id fk, start_line, end_line,
                  content text, token_count,
                  tsv tsvector, embedding vector(768)
                  -- indexes: hnsw (embedding vector_cosine_ops), gin(tsv),
                  --          gin(content gin_trgm_ops)

components        id pk, analysis_id fk, name, root_paths text[], file_count, plan_rank

summaries         id pk, analysis_id fk, scope ('component'|'repo'|'pr'|'change'),
                  component_id fk null, prose_md, structured jsonb,
                  model, prompt_version, embedding vector(768), created_at

pipeline_events   id bigserial pk, analysis_id/session_id, kind, payload jsonb,
                  created_at              -- durable event log for reconnect catch-up
```

## Experience side

```
users             id pk, subject text unique,   -- from Auth:Mode identity
                  display_name, created_at

sessions          id pk, user_id fk, analysis_id fk,
                  kind ('repo'|'pr'), title,
                  status ('queued'|'analyzing'|'ready'|'partial'|'failed'),
                  failure_reason null, created_at, last_opened_at
                  -- the left-pane history; ownership boundary for all scoped routes

experiences       id pk, session_id fk, version int,  -- refresh → v(n+1)
                  commit_sha, model_name, generated_at,
                  repo_summary jsonb                  -- languages, loc, license, vitals

sections          id pk, experience_id fk,
                  parent_section_id null,             -- deep dives hang off a parent
                  ord int, depth int default 0,
                  slug text,                          -- stable across versions:
                                                      -- progress carries over by slug
                  kind ('intro'|'architecture'|'scenario'|'build'|'test'|'deploy'|
                        'pr-overview'|'pr-walkthrough'|'pr-risk'|'deep-dive'),
                  title, summary, estimated_minutes,
                  status ('pending'|'generating'|'ready'|'failed')

blocks            id pk, section_id fk, ord int,
                  type ('markdown'|'diagram'|'code'|'callout'|'quiz'|'deep-dive-link'),
                  data jsonb                          -- typed payloads, see 05-ux.md

quizzes           id pk, section_id fk unique, title
quiz_questions    id pk, quiz_id fk, ord, type ('single'|'multi'|'boolean'|'short'),
                  prompt, data jsonb,                 -- answer keys/rubrics server-only
                  section_ref null, block_ref null    -- "reread X" targets

quiz_attempts     id pk, quiz_id fk, user_id fk, answers jsonb, submitted_at,
                  status ('grading'|'graded'), score_pct null, feedback jsonb

section_progress  (user_id, section_id) pk,
                  state ('unread'|'read'|'skipped'|'completed'),
                  quiz_best_pct null, updated_at

qa_threads        id pk, session_id fk, user_id fk, title, created_at
qa_messages       id pk, thread_id fk, ord, role ('user'|'assistant'),
                  content text,                       -- partial while streaming,
                                                      -- flushed ~1/s
                  citations jsonb,                    -- [{path,startLine,endLine,chunkId}]
                  status ('streaming'|'complete'|'error'|'cancelled'),
                  section_context null,               -- section open when asked
                  prompt_tokens, completion_tokens, created_at
```

## Design notes

- **Blocks are JSONB, not columns-per-type** — the generator evolves fast and the SPA
  is the only consumer. Sections, quizzes, and progress are relational because they
  are queried, ordered, and joined per user.
- **Section `slug` gives cross-version continuity**: when a session is refreshed
  (experience v(n+1) at a newer SHA), per-user progress carries over by slug.
- **Code excerpt blocks snapshot the actual lines** at generation time, so content
  renders even without GitHub reachability, and deep links use
  `github.com/{owner}/{repo}/blob/{sha}/{path}#L{a}-L{b}` — SHA-pinned, never breaks.
- **Everything the pipeline produces is the knowledge base** — nothing is re-derived
  at question time. Q&A retrieval spans chunks, summaries, and sections (plus diagram
  specs, so the expert can reference "see diagram 2").
- **Growth is bounded**: deleting a session cascades its analysis's chunks/embeddings
  (the big rows); disk workspaces are a cache reaped by the Orchestrator retention
  service; finished jobs are purged.
- The content-addressed **object store** (filesystem) holds immutable pipeline
  artifacts (repo-map JSON, architecture JSON) outside the database.
