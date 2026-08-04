#!/usr/bin/env bash
# Local development stack: PostgreSQL (compose), the gateway, both workers, the
# orchestrator, and the Vite dev server. Ctrl+C stops everything this script
# started (a Postgres container that was already running is left running).
#
# Environment overrides:
#   LLM_BASE_URL   OpenAI-compatible endpoint for generation/embeddings
#                  (default http://localhost:11434/v1 — point it at your Ollama)
#   LLM_MODEL      generation model (default qwen3-coder:oc)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LLM_BASE_URL="${LLM_BASE_URL:-http://localhost:11434/v1}"
LLM_MODEL="${LLM_MODEL:-qwen3-coder:oc}"
PIDS=()
STARTED_POSTGRES=0

cleanup() {
  echo
  echo "Stopping dev stack…"
  # ${PIDS[@]+…} keeps macOS bash 3.2's set -u happy when nothing started yet.
  for pid in ${PIDS[@]+"${PIDS[@]}"}; do
    kill "$pid" 2>/dev/null || true
  done
  wait 2>/dev/null || true
  if [ "$STARTED_POSTGRES" = "1" ]; then
    docker compose -f "$ROOT/deploy/compose.yaml" stop postgres
  fi
  echo "Done."
}
trap cleanup EXIT INT TERM

echo "==> PostgreSQL"
if [ -z "$(docker compose -f "$ROOT/deploy/compose.yaml" ps -q postgres 2>/dev/null)" ]; then
  STARTED_POSTGRES=1
fi
docker compose -f "$ROOT/deploy/compose.yaml" up postgres -d --wait

echo "==> Building solution"
dotnet build "$ROOT/codeexploder.slnx" --nologo -v q

echo "==> Starting services"
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5080 \
  Seed__Dir="$ROOT/seeds" Llm__BaseUrl="$LLM_BASE_URL" \
  dotnet run --project "$ROOT/src/CodeExploder.Gateway" --no-build &
PIDS+=($!)

dotnet run --project "$ROOT/src/CodeExploder.Workers.Analysis" --no-build &
PIDS+=($!)

Llm__BaseUrl="$LLM_BASE_URL" Llm__Model="$LLM_MODEL" \
  dotnet run --project "$ROOT/src/CodeExploder.Workers.Llm" --no-build &
PIDS+=($!)

dotnet run --project "$ROOT/src/CodeExploder.Orchestrator" --no-build &
PIDS+=($!)

echo "==> Waiting for the gateway"
for _ in $(seq 1 30); do
  curl -sf http://localhost:5080/healthz >/dev/null 2>&1 && break
  sleep 1
done

echo "==> Vite dev server"
if [ ! -d "$ROOT/webui/node_modules" ]; then
  (cd "$ROOT/webui" && npm install)
fi
(cd "$ROOT/webui" && npm run dev) &
PIDS+=($!)

echo
echo "Dev stack is up:"
echo "  app       http://localhost:5173"
echo "  gateway   http://localhost:5080  (DevBypass auth)"
echo "  llm       $LLM_BASE_URL ($LLM_MODEL)"
echo "Press Ctrl+C to stop."
wait
