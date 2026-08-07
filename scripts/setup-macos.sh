#!/usr/bin/env bash
# One-shot macOS setup for running Code Exploder locally. Installs the toolchain
# (via Homebrew), a container runtime for Postgres, and a local Ollama serving the
# generation + embedding models. Idempotent: anything already present is skipped.
#
# After this finishes, start the stack with ./dev.sh (see the README).
#
# Model note: production uses qwen3-coder:oc, a custom Modelfile that only raises
# the context window of the official qwen3-coder:30b for a specific GPU. Locally we
# use the base qwen3-coder:30b directly and give it a large context through the
# OLLAMA_CONTEXT_LENGTH server setting instead of building the variant.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# Context window for the base model. The synthesis stage packs ~45k tokens, so the
# default (a few k) would truncate it. 65536 covers every stage; lower it (e.g.
# 32768) on a memory-constrained Mac at some quality cost on large repositories.
OLLAMA_CTX="${OLLAMA_CONTEXT_LENGTH:-65536}"
GEN_MODEL="qwen3-coder:30b"
EMBED_MODEL="nomic-embed-text"

say()  { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '\033[33m    %s\033[0m\n' "$*"; }

[ "$(uname -s)" = "Darwin" ] || { echo "This script is for macOS. See the README for other platforms." >&2; exit 1; }

# --- Homebrew ---------------------------------------------------------------
if ! command -v brew >/dev/null 2>&1; then
  echo "Homebrew is required. Install it from https://brew.sh, then re-run." >&2
  exit 1
fi

say "Installing toolchain (git, gh, node, .NET SDK, Ollama)"
for f in git gh node dotnet ollama; do
  if brew list --formula "$f" >/dev/null 2>&1 || command -v "$f" >/dev/null 2>&1; then
    info "$f already present"
  else
    info "installing $f"
    brew install "$f"
  fi
done

# .NET 10 is required (global.json pins the SDK band).
if command -v dotnet >/dev/null 2>&1; then
  dnv="$(dotnet --version 2>/dev/null || echo 0)"
  case "$dnv" in
    10.*) info ".NET SDK $dnv" ;;
    *) warn ".NET SDK is $dnv but 10.x is required. Install it: brew install dotnet (or https://dotnet.microsoft.com/download/dotnet/10.0)" ;;
  esac
fi

# --- Container runtime (Postgres + Testcontainers) --------------------------
say "Container runtime (for Postgres and tests)"
if docker version --format '{{.Server.Version}}' >/dev/null 2>&1; then
  info "docker daemon reachable"
else
  if ! command -v docker >/dev/null 2>&1; then
    info "installing colima + docker CLI (a lightweight, GUI-free runtime)"
    brew install colima docker docker-compose
  fi
  if command -v colima >/dev/null 2>&1; then
    info "starting colima"
    colima start
  else
    warn "No Docker daemon found. Start Docker Desktop (or 'colima start') before ./dev.sh."
  fi
fi

# --- Ollama: server env, service, models ------------------------------------
say "Configuring Ollama (context window + memory settings)"
# Persist for launchd-managed processes (brew services / the Ollama app). These
# match production's memory-savers so a big KV cache fits alongside the weights.
# || true: launchctl setenv can be unavailable on a headless/SSH session; don't
# abort setup over it (Ollama then falls back to its default, smaller context).
launchctl setenv OLLAMA_CONTEXT_LENGTH "$OLLAMA_CTX" || true
launchctl setenv OLLAMA_FLASH_ATTENTION 1 || true
launchctl setenv OLLAMA_KV_CACHE_TYPE q8_0 || true
info "OLLAMA_CONTEXT_LENGTH=$OLLAMA_CTX  OLLAMA_FLASH_ATTENTION=1  OLLAMA_KV_CACHE_TYPE=q8_0"

say "Starting the Ollama service"
brew services restart ollama >/dev/null 2>&1 || brew services start ollama >/dev/null 2>&1 || true
for _ in $(seq 1 30); do
  curl -sf http://localhost:11434/api/tags >/dev/null 2>&1 && break
  sleep 1
done
if ! curl -sf http://localhost:11434/api/tags >/dev/null 2>&1; then
  warn "Ollama isn't answering on :11434 yet. Start it with 'ollama serve' (or 'brew services start ollama'), then re-run to pull models."
  exit 1
fi

say "Pulling models (large — first run downloads several GB)"
for m in "$GEN_MODEL" "$EMBED_MODEL"; do
  if ollama list 2>/dev/null | awk '{print $1}' | grep -qx "$m"; then
    info "$m already pulled"
  else
    info "pulling $m"
    ollama pull "$m"
  fi
done

# --- Local defaults for dev.sh ----------------------------------------------
# dev.sh sources .env.local (gitignored). Point it at the base model so plain
# ./dev.sh uses your local Ollama without needing env vars each time.
if [ ! -f "$ROOT/.env.local" ]; then
  say "Writing .env.local (local dev defaults)"
  cat > "$ROOT/.env.local" <<EOF
# Local dev defaults, sourced by dev.sh (gitignored). Edit freely.
LLM_MODEL=$GEN_MODEL
# Analyze a PRIVATE repo you can access: set REPO to owner/name (or a URL) and
# dev.sh will use your gh credentials so the workers can clone it. Requires
# 'gh auth login'. Leave unset for public repos.
# REPO=your-org/your-private-repo
EOF
  info "wrote $ROOT/.env.local"
fi

say "Setup complete"
cat <<EOF

  Next steps:
    ./dev.sh                     start the full stack (app at http://localhost:5173)
    REPO=org/name ./dev.sh       also enable analyzing that private repo (see README)

  The 30B model wants a fair amount of memory — Apple Silicon with >=32 GB unified
  memory is recommended. On a smaller Mac, re-run with a lower context, e.g.:
    OLLAMA_CONTEXT_LENGTH=32768 scripts/setup-macos.sh
EOF
