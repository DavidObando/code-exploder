# 09 — MCP server

The knowledge base as MCP tools (docs/08 §M8): `CodeExploder.Mcp` is a
dependency-free stdio MCP server that adapts the Gateway HTTP API — the KB keeps
exactly one contract. Remote access needs no extra hosting: point the adapter at the
deployed hostname and it rides the same edge auth gate as the web UI.

## Tools

| Tool | Does |
|---|---|
| `list_sessions` | Session ids/titles/statuses — the entry point |
| `get_repo_summary` | Vitals: description, languages, build systems, components |
| `list_sections` | The tutorial TOC (slug/kind/status/title) |
| `get_section` | One section rendered as markdown (prose, code, diagrams + narration) |
| `search_knowledge_base` | The M4 retrieval fusion over code/docs, file:line-cited |
| `ask_expert` | The Q&A loop, polled to completion (up to ~2 min), with citations |

## Configuration

| Env | Meaning |
|---|---|
| `CX_BASE_URL` | Gateway root (default `http://localhost:5080`) |
| `CX_BASIC_AUTH` | `user:password` for the deployed basicAuth edge gate; omit for local DevBypass |

Claude Code registration (local dev):

```bash
claude mcp add code-exploder -- dotnet run --project src/CodeExploder.Mcp
```

Remote (the home deployment):

```bash
claude mcp add code-exploder \
  -e CX_BASE_URL=https://code-exploder.kirkland.obando.io \
  -e CX_BASIC_AUTH=david:<password> \
  -- dotnet run --project src/CodeExploder.Mcp
```

## Notes

- The server speaks newline-delimited JSON-RPC (MCP stdio) with a hand-rolled,
  dependency-free loop; `tools/call` responses are text content. Native
  streamable-HTTP hosting inside the Gateway is future work.
- `search_knowledge_base` rides `GET /api/sessions/{id}/search`, which embeds the
  query in-gateway — the Gateway needs `Llm__BaseUrl` reachable (it joins the AI
  network in the deployed compose).
