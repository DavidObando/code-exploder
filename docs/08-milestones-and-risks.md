# 08 — Milestones and risks

## Milestones

Each milestone is independently demoable.

- **M0 — Walking skeleton.** Solution scaffold + design-token shell + Postgres +
  migration runner + job queue (with counting-join extension) + event bus/hub +
  compose. A `noop` pipeline streams fake progress end-to-end: submit → queued →
  stages tick → "ready". Proves the whole nervous system before any real analysis.
- **M1 — Deterministic analysis.** S0–S3: clone, repo map, chunking, component
  detection, run planning. The progress view shows a real narration ticker fed by
  deterministic facts — no LLM yet.
- **M2 — Tutorial generation.** LLM client + S4–S6: component summaries, architecture
  synthesis, diagram specs + the staged-reveal renderer, tutorial sections with
  verified citations; progressive publish; tutorial view with TOC/pacing.
- **M3 — Quizzes.** S7 generation, quiz UI, deterministic grading + the rubric
  grading job.
- **M4 — Q&A expert.** Embedding stages, retrieval fusion, streaming chat with
  citations.
- **M5 — PR-diff mode.** Diff mapping, incremental planning, overlay diagrams, PR
  walkthrough.
- **M6 — Deploy + hardening.** HomeInfra role/route/DNS, SharedGate auth, retention,
  degradation ladder + idle/overnight lane (night-shift upgrades), honest ETAs,
  backups.
- **M7 — Seeded demo report.** A pre-baked analysis of a demo repository packed with
  the deployment for product-review purposes: an export/import bundle format for a
  completed analysis (experience content, summaries, diagrams, quizzes, KB rows) plus
  a seeding step (startup job or CLI) that installs it as a ready session — reviewers
  see the full experience immediately, no GPU or wait required. The bundle format
  doubles as the backup/restore unit for analyses.
- **M8 — MCP server.** Expose the knowledge base to MCP clients, so agents and IDEs
  can interact with what an analysis produced: an MCP server speaking stdio (local)
  and streamable HTTP (remote) that wraps the Code Exploder APIs — tools such as
  list-sessions, get-repo-summary, get-section, search-knowledge-base (vector + FTS
  retrieval), and ask-expert (the Q&A loop, streamed). Remote access rides the same
  Auth:Mode gate as the web UI; the server is a thin adapter over the Gateway API so
  the KB has exactly one contract.
- **M9 — Origin story.** The Song Exploder moment: take the product apart, and piece
  by piece, tell the story of how it was made — from the git history. A full-depth
  history pass (unlike the shallow analysis clone) mines the repository's life
  deterministically: era segmentation (bursts, lulls, refactors, pivots), the birth
  commit of each component, key "moments" (first test, first CI, big rewrites,
  dependency shifts), and the cast of contributors. The LLM then narrates it as a
  chaptered, self-paced story section — "how this codebase came to be" — with a
  timeline visualization, architecture diagrams that evolve era by era (reusing the
  staged-diagram renderer with time as the reveal axis), and citations pointing at
  the actual commits and the files they introduced. Quizzes and Q&A extend naturally
  ("when and why was the queue introduced?"). Deterministic history mining rides the
  cpu lane; narration is a section-generation variant on the gpu lane.

## Risk register

| Risk | Mitigation |
|---|---|
| **GPU contention with other Ollama consumers** — ETAs blow up, models get evicted | Readiness gate + long `keep_alive`; live tok/s measurement re-forecasts ETAs; generous per-call timeouts with retry; quick mode on the smaller model; document the soft-ownership assumption |
| **Q&A latency behind batch jobs** (non-preemptive queue) | Every batch job ≤ ~90 s by construction (token ceilings); queue-position feedback in UI; future option: a second Ollama parallel slot reserved for interactive work |
| **Hallucination** (summaries, citations, answers) | Schema-validated JSON everywhere with repair-retry; citation tokens resolved against the real file table (invalid → dropped + logged); Q&A restricted to packed sources with an explicit "not in KB" instruction; code excerpts snapshot real lines |
| **Diagram quality** — the LLM picks poor abstractions (syntax is guaranteed by construction) | Spec seeded from the deterministic component graph — the LLM edits/labels/orders rather than invents; referential validation; a per-diagram "regenerate" action re-runs one job |
| **Long first run (~1 h)** discourages users | Progressive publish (intro ~20 min); honest plan-derived ETAs; narration ticker; quick mode; PR mode is minutes when a base analysis exists |
| **SSRF / abuse via arbitrary URLs** | Accept only `https://github.com/…` public repos; GitHub API pre-check + size check before cloning; no submodule recursion, no hooks; quota'd workspaces wiped after indexing |
| **GitHub anonymous rate limits** (60 req/h) | Clone-first design uses ~5–7 API calls per analysis; optional PAT env var; backoff on 403 |
| **Quiz answer-key correctness** (LLM may write wrong keys) | Explanations shown after answering so users can self-verify; short answers that fail grading are excluded from the score, never marked wrong; v2: a cheap self-check pass |
| **Regex chunker / import-graph imprecision** | Used only for ranking/grouping, never as ground truth in prose; tree-sitter is a contained v2 upgrade |
| **DB / disk growth** | Per-session cascade delete of analysis data; embedding sampling caps; Orchestrator retention for workspaces and finished jobs |
| **Join-counter edge cases** (reaped child double-decrement) | Decrement only on transition into a terminal status, guarded by the `status='blocked'` predicate under row locking; dedicated queue tests |
| **Monorepos exceeding caps** | Degradation ladder; subtree-scope parameter in v1; idle-lane night-shift upgrades |
| **Stale content after repo updates** | Everything SHA-pinned; explicit refresh → experience v(n+1) with progress carry-over by slug; "N commits behind" banner (cheap compare call, cached) |

## Open items

- Multi-user concurrency: two simultaneous analyses share one GPU serially — accepted
  in v1 (priority decay keeps runs roughly FIFO; position-in-line surfaced).
- Deduping analyses of the same repo across users into a shared experience with
  per-user progress — the schema already separates progress from content, so this is
  an additive change.
- PR sessions linking to an existing base-repo session in the UI.
- Retention policy defaults for failed sessions and workspaces.
