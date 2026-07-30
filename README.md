# Code Exploder

A self-hosted web app that onboards engineers to a codebase — or explains a large PR —
by generating a tutorial-like, self-paced interactive experience: "a 1:1 session with
an expert in the codebase."

Paste a public GitHub repo (or PR) URL and Code Exploder analyzes it with locally
hosted LLMs, then serves:

- an **intro** to what the code is and what the product does;
- a **whiteboard architecture tour** — diagrams that progressively reveal complexity,
  walking through data flows, user interactions, and product scenarios;
- a description of **how the code is built, tested, and released**;
- optional **quizzes** per section to check understanding;
- an interactive **Q&A chat** with the virtual expert, grounded in the analysis
  knowledge base, with file/line citations.

Everything runs on your own hardware: .NET 10 + React, PostgreSQL (+pgvector), and a
local Ollama endpoint. Inference is queued so a single shared GPU is never overwhelmed.

## Milestones

The core product (M0–M4) works end-to-end today. Full descriptions in
[docs/08](docs/08-milestones-and-risks.md).

| | Milestone | What it delivers |
|---|---|---|
| ✅ | **M0 — Walking skeleton** | Solution scaffold, design-token shell, Postgres job queue with counting joins, NOTIFY → SignalR event relay, compose stack |
| ✅ | **M1 — Deterministic analysis** | Shallow size-guarded clone, repo map (languages, build systems, entry points, churn), FTS-indexed chunking, component detection, repo-vitals card |
| ✅ | **M2 — Tutorial generation** | Component summaries → architecture synthesis → progressively published sections with staged-reveal whiteboard diagrams and verified code citations |
| ✅ | **M3 — Quizzes** | Per-section checks: auto-graded questions + one LLM-graded short answer (binary key-point coverage; ungradable ≠ wrong), retakes, ≥75 % auto-completes |
| ✅ | **M4 — Q&A virtual expert** | pgvector knowledge base, vector+FTS+trigram retrieval with RRF fusion, token-streamed answers with peekable file/line citations |
| ⬜ | **M5 — PR-diff explainer** | Paste a PR URL: incremental analysis, overlay diagrams badging changed nodes, review-order walkthrough |
| 🔄 | **M6 — Deploy + hardening** | Home-infrastructure deployment (Traefik ingress, SOPS secrets, published images), production auth gate, retention |
| ⬜ | **M7 — Seeded demo report** | A pre-baked analysis bundle installed as a ready session for product review; doubles as analysis backup/restore |
| ⬜ | **M8 — MCP server** | The knowledge base as MCP tools (stdio + streamable HTTP) for agents and IDEs: search, summaries, sections, ask-expert |
| ⬜ | **M9 — Origin story** | The Song Exploder moment: mine the full git history and tell, piece by piece, the story of how the product was made — eras, key commits, evolving architecture diagrams, narrated as a chaptered story |

The LLM defaults to `qwen3-coder:oc` via an OpenAI-compatible endpoint
(`Llm__BaseUrl`, default `http://localhost:11434/v1`); the embedding/generation
GPU-co-residency budget is documented in [docs/06](docs/06-llm-strategy.md).

### Dev quickstart

```bash
docker compose -f deploy/compose.yaml up postgres -d   # Postgres 17 + pgvector on :5433
dotnet run --project src/CodeExploder.Gateway           # API + hub on :5080 (DevBypass auth)
dotnet run --project src/CodeExploder.Workers.Analysis  # deterministic-analysis worker
dotnet run --project src/CodeExploder.Workers.Llm       # generation + embedding lanes (needs Ollama)
cd webui && npm install && npm run dev                  # Vite dev server, proxies to :5080
```

Tests: `dotnet test codeexploder.slnx` (queue join semantics run against a throwaway
Testcontainers Postgres — Docker required) and `cd webui && npm test`.

## Design

The initial system design study lives in [`docs/`](docs/00-overview.md):

| Doc | Contents |
|---|---|
| [00-overview](docs/00-overview.md) | Context, product flow, guiding principles, system architecture, solution layout |
| [01-analysis-pipeline](docs/01-analysis-pipeline.md) | The job DAG from repo URL to tutorial; GitHub API usage; PR-diff mode |
| [02-queue-and-events](docs/02-queue-and-events.md) | Postgres job queue with counting joins; priorities; idle lane; event bus |
| [03-data-model](docs/03-data-model.md) | PostgreSQL schema (analysis side + experience side) |
| [04-api](docs/04-api.md) | HTTP API surface and SignalR event contract |
| [05-ux](docs/05-ux.md) | Pages, content model, progressive whiteboard, quizzes, chat |
| [06-llm-strategy](docs/06-llm-strategy.md) | Model selection, context packing, token budgets, RAG design |
| [07-deployment](docs/07-deployment.md) | Home-infrastructure deployment, ingress, secrets |
| [08-milestones-and-risks](docs/08-milestones-and-risks.md) | Milestones M0–M9, risk register |

## License

[MIT](LICENSE)
