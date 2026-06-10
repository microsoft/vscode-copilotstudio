# Sync Semantics Reference — CLI-layered layout

> Disk layout and per-entity-type reference for **CLI-layered** agents
> (`AuthoringShape.CliCopilot`) — the three-layer `behaviors/` · `capabilities/` ·
> `infrastructure/` layout.
>
> Classic agents use the historical layout: see
> [SYNC-SEMANTICS-CLASSIC.md](./SYNC-SEMANTICS-CLASSIC.md). The shape-neutral sync
> mechanics (operations pipeline, hidden state, reusable/managed filtering, change
> detection, pac parity) live in [SYNC-SEMANTICS.md](./SYNC-SEMANTICS.md).

---

## How a CLI agent is detected

Disk location is **keyed on `AuthoringShape`** (TDD D19/D20) — *not* a one-way classic→CLI
conversion. Two independent signals are involved, and they are deliberately decoupled (D29):

- **Shape (identity)** is resolved by `AgentClassifier` (`DetectAuthoringShape` → `ClassifyCloud`).
  The preferred signal is the typed `BotConfiguration.AgentSettings` block (present ⇒ `CliCopilot`,
  D15/D19/D25). If `AgentSettings` is absent, detection falls back to the **template prefix**: a CLI
  template prefix ⇒ `CliCopilot`, a classic template prefix ⇒ `Classic`, an unrecognized template ⇒
  a *provisional* verdict that fails closed on push/reattach (D35), and no template ⇒
  classic/unknown. There is no filename-based kind discrimination (D22/D25): both shapes use
  `settings.mcs.yml` as the entity file.
- **Layout** is declared by the `agent.sync.yaml` marker (D29): a generic-YAML `.sync.yaml` file
  (never MCS-parsed) carrying `layoutVersion` only. It is **layout-authoritative, never
  shape-authoritative** — even when present, `AuthoringShape` is still derived from
  `settings.mcs.yml`; the marker must not short-circuit shape detection, and a marker/content
  mismatch fails closed. Classic clones never emit it.

The marker is written **for CLI agents only**, on clone, by `WorkspaceSynchronizer`
(`IsCliAgentEntity` ⇒ write `agent.sync.yaml`); classic agents emit nothing, preserving classic
byte-identity.

---

## Workspace layout

After a clone, a CLI-layered agent workspace looks like this:

```
agent.sync.yaml                              ← workspace-layout marker (D29): layoutVersion only,
                                               .sync.yaml (generic YAML, never MCS-parsed); CLI-only
settings.mcs.yml                             ← agent entity + identity (BotEntity) — the CLI entity (D22)
icon.png                                     ← agent icon (from BotEntity.IconBase64)
references.mcs.yml                           ← component collection schema name references (when applicable)
topics/{name}.mcs.yml                        ← topics (classic fallback — unchanged)
actions/{name}.mcs.yml                       ← task dialogs (classic fallback — unchanged)
agents/{agentName}/…                         ← sub-agent folders (dialog is classic agents/{name}/agent.mcs.yml; content nests CLI-shaped)
behaviors/{name}.mcs.yml                     ← InlineAgentSkill (inline skills), infix .skill. (D21)
capabilities/tools/{name}.mcs.yml            ← ConnectorTool / WorkflowTool / McpTool /
                                               ConnectedAgentTool, infix .tool. (D10/D21)
capabilities/knowledge/{name}.mcs.yml        ← knowledge sources, infix .knowledge. (D21)
capabilities/knowledge/files/{name}.mcs.yml  ← file-attachment metadata; the downloaded content
                                               file sits in the SAME directory, infix .file. (D21/D34)
infrastructure/connections/{name}.sync.yaml  ← connection references (D27/D28): Sync-managed
                                               .sync.yaml overlay, NOT a projected component
.mcs/                                        ← hidden state (unchanged from classic — see the index doc)
```

Two classic root files are **absent** for a CLI agent:

- **`agent.mcs.yml`** — the classic `GptComponentMetadata` sentinel is retired for CLI (D22); clone
  deliberately does *not* fabricate a default one (that would create a phantom `GptComponent`).
  Identity and instructions live in `settings.mcs.yml`.
- **`connectionreferences.mcs.yml`** — replaced by the per-reference
  `infrastructure/connections/*.sync.yaml` overlay (below).

Element types with **no CLI override** fall back to the classic folders and behave exactly as in
the [classic reference](./SYNC-SEMANTICS-CLASSIC.md): topics, actions, sub-agent dialogs, global
variables, bot settings, entities, external triggers, translations, environment variable
definitions, and workflows. The CLI overrides come from `LspProjection.CliRules`, which is consulted
before the shared classic rules **only** when the shape is `CliCopilot` (so classic projection stays
byte-identical, D20).

> Note: the CLI `behaviors/` folder holds the **`InlineAgentSkill`** type — distinct from the
> classic BotFramework `SkillComponent`, which still routes to `skills/`. Both use the `.skill.`
> infix but are different OM types in different folders.

> **Sub-agents.** The sub-agent *dialog* (`AgentDialog`) projects to `agents/{agentName}/agent.mcs.yml`
> via the classic rule (not CLI-overridden — `LspProjection.GetFilePath`). What is shape-keyed is the
> sub-agent's *content*: its own behaviors / tools / knowledge nest **under** `agents/{agentName}/`
> using the CLI three-layer (e.g. `agents/{agentName}/capabilities/knowledge/files/…`), mirroring the
> top-level agent.

---

## Entity type reference (CLI-specific paths)

Only the entity types below move for a CLI agent. **Everything not listed keeps its classic path
and flow** (see the classic reference). The mapping is from `LspProjection.CliRules` and
`CliAgentConnectionsWriter`.

| Entity type | Classic path | CLI path |
|---|---|---|
| Agent metadata (`GptComponentMetadata`) | `agent.mcs.yml` | *absent* — retired for CLI (D22); identity is `settings.mcs.yml` |
| Inline skills (`InlineAgentSkill`) | n/a (CLI-only type) | `behaviors/*.mcs.yml` (D21) |
| Tools (`ConnectorTool` / `WorkflowTool` / `McpTool`) | n/a (CLI-only type) | `capabilities/tools/*.mcs.yml` (D21) |
| Connected agents (`ConnectedAgentTool`) | `agents/*.mcs.yml` (classic `InvokeConnectedAgentTaskAction`) | `capabilities/tools/*.mcs.yml` (D10) |
| Knowledge sources | `knowledge/*.mcs.yml` | `capabilities/knowledge/*.mcs.yml` (D21) |
| File attachments | `knowledge/files/*.mcs.yml` | `capabilities/knowledge/files/*.mcs.yml` (D34) |
| Connection references | `connectionreferences.mcs.yml` | `infrastructure/connections/*.sync.yaml` (D27/D28) |

### Agent identity (`settings.mcs.yml`) and the retired `agent.mcs.yml`

`settings.mcs.yml` is the single agent entity **and** identity for CLI agents (D22) — same root path
as classic, but for CLI it is also what `AgentClassifier` reads to derive `AuthoringShape`. There is
**no** `agent.mcs.yml`: clone skips the default-`GptComponentMetadata` write for CLI agents so it
cannot fabricate a phantom `GptComponent`. The VS Code clone post-open opens `settings.mcs.yml` for
CLI agents (D36).

### Inline skills → `behaviors/` and tools → `capabilities/tools/`

CLI behaviors (`InlineAgentSkill`, infix `.skill.`) project to `behaviors/`; CLI tools
(`ConnectorTool`, `WorkflowTool`, `McpTool`, infix `.tool.`) project to `capabilities/tools/`.
Connected agents are the typed `ConnectedAgentTool` (infix `.tool.connected-agent.`) and route to
`capabilities/tools/` as well (D10) — whereas the classic `InvokeConnectedAgentTaskAction` TaskDialog
routes to `agents/` (a projection RuleOverride), not `actions/`. Clone, push,
and pull run through the same component pipeline as classic; only the folder differs.

### Knowledge sources → `capabilities/knowledge/` and files → `capabilities/knowledge/files/`

Knowledge sources project to `capabilities/knowledge/` (D21); file-attachment metadata and the
downloaded binary content live together at `capabilities/knowledge/files/` (D34). The push-side
new-binary scan is shape-keyed to that folder for CLI agents (`CliKnowledgeFilesSubPath`), and the
VS Code client mirrors the path (`getFilesDir` in `knowledgeFiles/syncUtils.ts`). `PreserveBotPrefixedFiles`
keeps bot-prefixed display-name knowledge files as-is. Server-side push/pull behavior (e.g. silent
rejection of description-only edits) is the same as classic — see the classic reference.

### Connection references → `infrastructure/connections/*.sync.yaml`

Connection references are written **one file per reference** under `infrastructure/connections/` as a
Sync-managed `.sync.yaml` overlay (`CliAgentConnectionsWriter`): generic YAML, never MCS-parsed, and
excluded from the D30 component allowlist (it lives outside the component folders). Each file is the
byte-equivalent of a 1-item slice of the classic flat-file serializer (D28). Unlike classic — where
the flat `connectionreferences.mcs.yml` is rewritten from the definition and not diffed — CLI
connection references **participate in the push/pull diff** (Node F): on read they are overlaid with
cloud-only refs (so references the user did not author locally are preserved), and the diff emits
Insert / Update / Delete changes. Two boundaries apply, stated inline:

- **Delete detection by direction (D32).** A **local-push** diff derives the "present" reference set
  from the on-disk files (`CliAgentConnectionsReader.ListDiskLogicalNames`), gated on the adopted CLI
  layout so a pre-CLI clone never synthesizes a phantom delete. A **remote-pull** preview treats the
  new cloud snapshot as authoritative and does **not** consult local disk (a ref in the old cloud
  cache but absent from the new cloud state is an incoming remote delete; a local-only on-disk delete
  is not an incoming remote change).
- **Atomic write ordering (D33).** On write, the per-reference set is committed **first**; only then
  is any leftover flat `connectionreferences.mcs.yml` (from a prior classic-shape clone) deleted. An
  interrupted write therefore never leaves *both* gone — which would make the next push read zero
  references on disk and synthesize a delete for every cloud ref. The writer also pre-prunes
  `infrastructure/connections/` so orphan files from a prior write are removed (correct even in the
  0-reference case).

---

## Clone / push divergences (summary)

Relative to the shared [operations pipeline](./SYNC-SEMANTICS.md):

- **Clone** writes the `agent.sync.yaml` layout marker (D29) and does **not** write a default
  `agent.mcs.yml` (D22); component bodies project to `behaviors/`, `capabilities/tools/`,
  `capabilities/knowledge/`, `capabilities/knowledge/files/`; connection references are written as
  the per-reference `infrastructure/connections/*.sync.yaml` overlay instead of the flat file.
- **Push/pull** diff connection references (overlay-on-read, directional delete detection D32,
  atomic write ordering D33, above). All other component types use the same changeset / 3-way-merge
  mechanics as classic.

---

## Validation status

The CLI layout, the shape-keyed projection (`LspProjection.CliRules`), the connection-reference
writer/reader/overlay, and the CLI push/pull diff are covered by the unit suites (236 Sync + 741 LSP)
as of this writing. The **end-to-end live round-trip** on a component-rich CLI agent (behaviors +
tools + knowledge + connections) with Maker fidelity is **pending operator-gated live
re-validation** and is not yet re-confirmed live. Treat live CLI push as unverified until that step
closes; this doc documents the intended final semantics, and any area not yet exercised end-to-end
is called out here rather than implied as supported.
