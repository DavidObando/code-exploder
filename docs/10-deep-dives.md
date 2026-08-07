# 10 — Deep dives (recursive scope explosion, M10)

The main tour is an end-to-end view. Deep dives let a learner **zoom into one
sub-system**: exploding a scope (a detected component) generates a nested
mini-tour — its internal architecture, one or two internal flows, and its
boundary with the rest of the system — recursively, down to `MaxDepth` (3).

## Model

- **Explosion unit = component.** One explosion per `(experience, component)`
  (unique index — the dedup key). Sub-components are detected on demand by
  re-running `ComponentDetector` over the scope's file subtree
  (`DetectWithin`), stored with `parent_component_id`/`depth`, and are
  themselves explodable.
- A dive materializes as a **`deep-dive` parent section** (scope overview
  narration + scoped diagram, anchored under the `architecture` section — or
  under the parent dive for nested scopes) plus child sections
  (`deep-dive-tour`, `deep-dive-flow` ×≤2, `deep-dive-interfaces`) one depth
  level down. `sections.parent_section_id`/`depth` (dormant since V0003) carry
  the tree; the SPA renders a collapsible tree from `parentSectionId`.
- The `explosions` table (V0008) is the status machine:
  `queued → running → ready | partial | failed`, with per-dive
  `sections_ready/total`. Session progress still counts depth-0 sections only —
  dives never move the main tour's numbers or the analysis stage bars.

## Eager vs on-demand

- After `finalize-experience`, the top `EagerTopK` (2) components by
  **criticality** — `3·fanIn(TalksTo) + 2·risks + capped size + capped churn
  share` (`CriticalityScorer`) — explode automatically at priority −10: they
  only run when the gpu lane is idle, and never recurse.
- `POST /api/sessions/{id}/explode {componentId}` explodes on demand at
  priority 5 (config `Explosions:OnDemandPriority`): above fresh-session work,
  far below interactive grading/Q&A. A duplicate POST is idempotent (200); a
  queued eager dive gets upgraded to on-demand priority; a **failed** dive is
  reset and relaunched (POST-again is the retry; `POST
  /api/explosions/{id}/retry` does the same explicitly).
- Guard rails (`ExplosionOptions`): `MaxDepth 3 · MinScopeFiles 8 ·
  MaxSubComponents 8 · MaxChildSections 5 · MaxActivePerAnalysis 1`.

## Job flow

```
explode-scope (cpu lane, deterministic: re-clone if reaped, DetectWithin, insert subs)
  → summarize-component ×N (scoped branch) ⇒ counting-join ⇒ synthesize-scope
      (ScopeSynthesizer → scoped ArchitectureDoc, scope='scope' summary row;
       publishes the deep-dive parent section; plans children)
  → tutorial-section ×M (scoped: deeper prompt, citations restricted to scope files)
      ⇒ counting-join ⇒ finalize-scope (explosion → ready|partial, DeepDiveReady)
```

All jobs of a dive inherit its priority and carry `analysisId`, so session
retry/delete sweeps them; `explosions` rows cascade with the analysis.

**Failure policy:** a dead dive marks the explosion + its section `failed` and
emits `DeepDiveFailed` — it must never flip a served session's status
(`explode-scope` joins `history-mine` in the workers' post-completion
exemption; `synthesize-scope`/`finalize-scope` have explicit terminal-failure
cases). A dead child section leaves the dive `partial`.

## Events & UI

`DeepDivePlanned` / `DeepDiveReady` / `DeepDiveFailed` ride the normal session
event bus; child sections reuse `SectionReady` (payload now carries `depth` +
`parentSectionId`). The SPA:

- renders the TOC as a collapsible tree (`tocTree.ts` is the single source of
  tree shape and linear tour order — also the future multimedia playlist
  order); deep-dive branches default to collapsed, and the current section's
  ancestors are always auto-expanded;
- shows a small accent **unread dot** on every ready-but-never-viewed section,
  rolling up onto collapsed parents; it clears via the existing scroll-to-end
  auto-read;
- offers a **Go deeper** card on the architecture section and on every
  deep-dive section (via `GET /api/sessions/{id}/scopes?parentComponentId=`),
  with per-scope explode/generating/continue/retry states; toasts fire only
  for dives this tab initiated.
