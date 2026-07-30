# 06 — LLM strategy and knowledge base

## Constraints

A single 24 GB GPU on the Ollama host, shared with other consumers. Model swaps are
minutes-scale (VRAM reload), so the design never swaps mid-run. Available models
include custom wide-context variants maintained as IaC in the HomeInfra repo:
`qwen3-coder:oc` (30B, 70k ctx, ~22.3 GB), `gpt-oss:oc` (20B, 128k, ~14 GB),
`qwen3.6:oc` (27B, 128k, ~21 GB), `gemma4:oc` (256k). The embedding model
(`nomic-embed-text`, 768-dim, ~270 MB) is added to the same IaC and stays resident.

## Model selection (decided once per run, at plan time)

- **Default: `qwen3-coder:oc`** — strongest at code; every prompt in the plan is
  packed ≤ 55k so 70k context suffices; fits 24 GB alongside the embedder.
- **Escalate to `qwen3.6:oc`** only when the planner predicts the S5 synthesis input
  cannot fit 70k even after summary compression (> ~45 components).
- **`gpt-oss:oc`** is the quick/degraded mode: smaller resident footprint plays nicer
  with other GPU consumers; used when the operator enables quick mode or contention
  is high.
- **Q&A uses the model recorded on the analysis** — chat never triggers a swap.
- `gemma4:oc` is unused in v1: 256k context is unnecessary under the
  pack-don't-stuff strategy, and a small roster maximizes residency.
- Requests set a long `keep_alive` (~2h). A readiness gate probes Ollama before
  dispatching; runs park (rather than fail) when the LLM is unavailable.

## Context packing

Never "fill the window": packers assemble *ranked* material to a per-stage ceiling
(16k S4 · 55k S5 · 10–12k S6 · 20k Q&A) and truncate from the bottom of the ranking.
Big-context models are an escape hatch, not the plan — prefill time scales with prompt
length and the GPU is shared. Temperature is pinned to 0 for determinism; the client
ports the reference app's starved-output guard (reasoning models consuming the token
budget → empty content with `finish_reason=length` throws instead of reading as "no
findings").

## Expected load and degradation

2k-file repo ≈ 56 generation calls, ~750k prompt / ~83k output tokens (see the
[budget table](01-analysis-pipeline.md#token-budget-2k-file-repo--enforced-not-hoped)).
At 30B-class throughput (~500–800 tok/s prefill, ~25–35 tok/s decode): **~55–80 min**
end-to-end on an idle GPU, intro readable ~20 min in. Small repos: ~10–15 min.

**Degradation ladder** for very large repos, applied automatically by the planner and
surfaced to the user:
1. Tighten file ranking (fewer file-heads per component).
2. Cap components at 40 by merging (coarser architecture, same call count).
3. **Breadth mode**: skip S4 for low-rank components — represented by deterministic
   signatures only, flagged in the tutorial. (The idle lane can upgrade these
   overnight — see [02-queue-and-events](02-queue-and-events.md).)
4. Sample embeddings (rank-weighted) to cap chunk count at ~30k.
5. Above hard caps, refuse with guidance and offer the **subtree-scope parameter**
   ("point me at a subdirectory") — the monorepo story.

## Knowledge base & Q&A (RAG)

**Persisted knowledge** — nothing re-derived at question time: code chunks, doc
chunks, component summaries, tutorial sections (all embedded, pgvector HNSW cosine),
plus diagram specs and the parsed build/CI facts.

**Retrieval per question** (inside the `qa-answer` job):
1. Condense the question (folding the last 2 chat turns, for follow-ups) and embed it
   via `/api/embed` — milliseconds.
2. Three ANN searches scoped to the session's analysis id(s): summaries+sections
   (k=6), code chunks (k=12), doc chunks (k=6).
3. Lexical leg: FTS + `pg_trgm` similarity over chunk text for identifier-shaped
   tokens (code names embed poorly). Fuse with reciprocal-rank fusion.
4. Pack ~20k tokens: architecture-overview digest (always), top summaries, then code
   chunks, each block headed `[S{n}] {path}:{start}-{end}`.

**Answering:** system prompt — "answer only from the provided sources; cite `[Sn]`;
say so when the KB doesn't cover it." Single streamed call on the analysis's model.
Post-processing maps `[Sn]` markers to structured citations
`{path, startLine, endLine, chunkId}` persisted on the message; markers not present in
the pack are stripped (hallucination guard).

v1 is single-shot RAG — one GPU, tight latency budget. The LLM client's tool-call
support leaves the door open for a v2 agentic "search the KB again" loop.

**PR-mode retrieval:** filters `analysis_id IN (base, pr)` with PR-layer boosts; base
chunks superseded by changed files are masked.
