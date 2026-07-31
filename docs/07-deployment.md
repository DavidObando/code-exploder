# 07 — Deployment

Target: the home infrastructure described in the private **HomeInfra** repository.
Concrete addresses, ports, and host inventory live there — this doc records only the
shape. (Per the [docs hygiene rule](00-overview.md), no LAN details here.)

## Placement

The stack deploys to **ai-vm**, the Docker Compose VM that also hosts Ollama — so the
LLM is reachable as `http://ollama:11434` on the compose network with no LAN hop. The
GPU is shared with other Ollama consumers; Code Exploder assumes *soft* ownership
during a run (see risk register).

## Stack

`docker/code-exploder/compose.yaml` (source of truth in the HomeInfra repo):

```
gateway            (published host port for ingress)
workers-analysis
workers-llm
orchestrator
postgres           postgres:17 + pgvector, own volume
```

- Images are published to GHCR by `.github/workflows/publish.yml` on every push to
  main (`ghcr.io/<owner>/code-exploder-{gateway,worker-analysis,worker-llm,
  orchestrator}`, tagged `latest` + `sha-<commit>` for rollback), built from the
  single parameterized multi-stage `deploy/Dockerfile` (`--build-arg PROJECT=…`):
  a Node stage builds `webui/dist` (baked into the Gateway image), a .NET SDK stage
  publishes the selected project, runtime on `dotnet/aspnet:10.0` as a non-root user.
  The HomeInfra compose stack pulls these images; nothing builds on the target host.
- The LLM worker joins the AI stack's Docker network so it reaches Ollama by service
  name (`http://ollama:11434/v1`) — no cross-host hop, no addresses in config.
- Persistent data bind-mounted under `/mnt/ai/code-exploder/{pg,workspaces,objects}`,
  owner `1000:1000`, dirs `0775` (HomeInfra convention).
- Local dev inner loop mirrors the reference app: `compose up postgres -d`, run
  services from the IDE with `Auth:Mode=DevBypass`.

## Ingress & TLS

- Traefik v3 (running on the other VM) terminates TLS for the whole infra. Because
  Code Exploder runs on a different host than Traefik, routing uses the
  **file-provider** pattern: a new template
  (`ansible/roles/traefik/templates/code-exploder-services.yml.j2`, modeled on the
  existing ai-services template) routes `code-exploder.kirkland.obando.io` to the
  Gateway's published port on ai-vm.
- The wildcard Let's Encrypt cert already covers the hostname; the only DNS work is
  one **DNS-only CNAME** in Cloudflare (per HomeInfra's documented convention — no
  wildcard CNAME).
- The hostname is internet-reachable, so production runs `Auth:Mode=SharedGate`:
  Traefik's `basicAuth` middleware authenticates (credentials from a SOPS-managed
  htpasswd entry) and forwards the username via `headerField` in the
  `X-WebAuth-User` header, which the Gateway's SharedGate handler trusts as the
  identity. This is safe only because the gateway is reachable exclusively through
  that proxy. Ollama itself stays unexposed.

## Provisioning (Ansible)

A new `code_exploder` role modeled on the existing `ai_services` role:

1. Create data directories under `/mnt/ai/code-exploder/`.
2. Sync `docker/code-exploder/` to the guest compose path
   (`/opt/homeinfra/docker/code-exploder/`).
3. Template `.env` (mode 0600) from SOPS-encrypted group vars — DB password and any
   future tokens (e.g. optional GitHub PAT) are added to
   `ansible/group_vars/all.sops.yaml`.
4. `docker compose up -d` via `community.docker.docker_compose_v2`.

Wire the role into `ansible/playbooks/ai-vm.yml`. Add `nomic-embed-text` to the
Ollama model IaC list so the embedder is pulled idempotently.

## Operations

> **After restoring or recreating the database, restart every service.** Npgsql
> caches extension type OIDs (pgvector's `vector`) per data source at first
> connection; a worker that outlives a DB recreation fails with
> `XX000: cache lookup failed for type <oid>` on its next vector operation.

- **Backups**: Postgres is the only stateful store that matters (workspaces and the
  object store are re-derivable caches); add a `pg_dump` cron into the data mount,
  which is covered by HomeInfra's existing backup recommendations.
- **Retention**: the Orchestrator purges finished jobs, reaps dead-worker leases,
  deletes expired workspaces, and cascades deleted sessions' analysis data.
- **Observability v1**: `/api/system/status` (DB, Ollama reachability + loaded
  models, queue depth, worker heartbeats) feeds the UI StatusBar; structured logs to
  stdout for `docker logs`. Prometheus/Grafana can be added later following the
  reference app's ops profile.
