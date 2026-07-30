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

## Status

**M2 (tutorial generation) complete**: paste a public GitHub repo (or PR) URL and the
pipeline clones and deterministically maps it (M1), then a locally hosted LLM
generates the full tutorial experience — component summaries fan out into an
architecture synthesis, then sections publish progressively: an intro, an
architecture tour and product-scenario walkthroughs with **progressively revealed
whiteboard diagrams** (the LLM emits validated JSON specs; Mermaid is rendered
deterministically), verified file/line code citations, and a build/test/release
section — consumed self-paced with per-section progress tracking. **M3 adds quizzes**:
per-section comprehension checks (mostly auto-graded; one optional short answer graded
by the LLM as binary key-point coverage, with ungradable answers excluded rather than
marked wrong), unlimited retakes with best-score tracking, and automatic section
completion at ≥75 %. **M4 adds the Q&A virtual expert**: the knowledge base embeds on
a parallel lane (`nomic-embed-text`, co-resident with the generator), retrieval fuses
vector, full-text, and trigram-identifier search with reciprocal-rank fusion, and
answers stream token-by-token into a chat panel with verified file/line citations you
can peek inline or open on GitHub — grounded strictly in the analyzed sources.
PR-diff mode arrives in M5. Milestones M0–M8 are described in
[docs/08](docs/08-milestones-and-risks.md).

The LLM defaults to `qwen3-coder:oc` via an OpenAI-compatible endpoint
(`Llm__BaseUrl`, default `http://localhost:11434/v1`); the embedding/generation
GPU-co-residency budget is documented in [docs/06](docs/06-llm-strategy.md).

### Dev quickstart

```bash
docker compose -f deploy/compose.yaml up postgres -d   # Postgres 17 + pgvector on :5433
dotnet run --project src/CodeExploder.Gateway           # API + hub on :5080 (DevBypass auth)
dotnet run --project src/CodeExploder.Workers.Analysis  # noop pipeline worker
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
| [08-milestones-and-risks](docs/08-milestones-and-risks.md) | Milestones M0–M6, risk register |

## License

[MIT](LICENSE)
