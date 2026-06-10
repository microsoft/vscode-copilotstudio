# Sync Semantics Reference

> Definitive reference for how each entity type behaves on clone, push, and pull
> in the `CopilotStudio.Sync` shared library.
>
> Source of truth: the `CopilotStudio.Sync` library source code in this directory, cross-checked
> against the project's QA boundary map (19 entity types) and per-entity-type live-test findings
> (maintained outside this repository).

---

## How to read this document

Each entity type gets a section describing three things: where it lives on disk,
how it gets there, and what happens when you push it back. If a flow has a known
behavioral boundary (server silently rejects the change, for instance), the
boundary is stated inline with the flow description — not in a footnote, not in
a caveat appendix.

The audience is a developer who is intelligent but not yet familiar with this
codebase. If you already know how `PvaComponentChangeSet` works, the per-type
sections will be fast reading. If you don't, start with the operations overview.

---

## Document map

The on-disk layout and per-entity-type disk paths are **keyed on `AuthoringShape`** (TDD D19/D20).
Classic and CLI agents share **most** of the sync mechanics and differ mainly in *where* files live.
The one mechanics exception is **connection references**: for CLI they are a per-reference
`.sync.yaml` overlay that participates in the push/pull diff (overlay-on-read, directional delete
detection D32, atomic write ordering D33), whereas the classic flat `connectionreferences.mcs.yml`
is rewritten from the definition and not diffed the same way (see the CLI doc). To avoid one
document straddling both shapes, the layout / entity reference is split by shape:

- **[Classic layout & entity reference](./SYNC-SEMANTICS-CLASSIC.md)** — `AuthoringShape.Classic`
  (`agent.mcs.yml`, `connectionreferences.mcs.yml`, `knowledge/`, `actions/`, `skills/`, …),
  byte-identical to the historical layout.
- **[CLI-layered layout & entity reference](./SYNC-SEMANTICS-CLI.md)** — `AuthoringShape.CliCopilot`
  (the three-layer `behaviors/` · `capabilities/` · `infrastructure/` layout, `settings.mcs.yml`
  as identity, the `agent.sync.yaml` marker, and the `.sync.yaml` connection overlay).

Shape is derived from `settings.mcs.yml` content by `AgentClassifier` (typed `AgentSettings`
presence, with a template-prefix fallback — see the CLI doc; D19/D25); the `agent.sync.yaml` marker
declares the workspace **layout** (D29), not identity. Location is shape-keyed — *not* a one-way classic→CLI conversion; classic stays
byte-identical.

The per-entity-type sections (the "three things: where it lives, how it gets there, what push does"
format) live in the two shape docs above. **This file holds the shape-neutral mechanics shared by
both shapes**: the operations pipeline, hidden state (`.mcs/`), reusable/managed-component
filtering, change detection (except the CLI connection-reference diff — see the CLI doc), and pac
parity gaps. Where the pipeline below shows concrete filenames
it uses the classic layout; see the CLI doc for CLI paths and the marker / connection-overlay /
atomic-write divergences.

---

## Operations overview

### Clone

Entry point: `CloneChangesAsync` (single agent) or `CloneAllAssetsAsync` (agent + component collections).

1. Call Island Control Plane `GetComponentsAsync` with `changeToken: null` to get the full component set.
2. Apply the changeset to an empty `BotDefinition` or `BotComponentCollectionDefinition`.
3. Write hidden state: `.mcs/botdefinition.json` (cloud cache), `.mcs/changetoken.txt`, `.mcs/conn.json`, `.mcs/.gitignore`.
4. Write workspace files: `settings.mcs.yml`, `icon.png`, `agent.mcs.yml`, `connectionreferences.mcs.yml`, `references.mcs.yml`, all component `.mcs.yml` files, environment variables, and workflows.
5. If no `GptComponentMetadata` component exists in the changeset (newly created agent with no metadata), write a default empty `agent.mcs.yml`.
6. For multi-asset clones: second pass calls `ApplyTouchupsAsync` to resolve cross-workspace relative paths in `references.mcs.yml`.

Workflows are fetched from Dataverse separately (`GetWorkflowsAsync`) because they are not part of the Island Control Plane component set. Each workflow produces a `workflows/{name}-{guid}/` directory containing `metadata.yml` and `workflow.json`.

### Pull

Entry point: `PullExistingChangesAsync`.

1. Fetch workflows from Dataverse and merge into the definition.
2. Compute local changes (workspace files vs cloud cache) and remote changes (Island CP with change token).
3. Detect conflicts: components edited both locally and remotely (matching schema names).
4. For each conflict, perform a 3-way merge:
   - YAML content: structural diff/merge via `DiffFinder`/`MergeOutput` with whitespace-insensitive comparison.
   - DisplayName/Description metadata: if local unchanged from original → use remote; if remote unchanged → use local; if both changed → remote wins.
5. Handle BotEntity conflict separately: 3-way merge on settings YAML, then restore non-settings properties (including `IconBase64`) from the remote entity.
6. Filter out components structurally identical to the pre-pull cloud cache snapshot. This prevents silently overwriting local edits when the server returns a full component set instead of just deltas.
7. Download knowledge files (file attachments) from blob storage, max 5 parallel.
8. Update cloud cache, write new change token, write workspace files.

### Push

Entry point: `PushChangesetAsync`.

1. Call Island Control Plane `SaveChangesAsync` with the local changeset. This is atomic: the server receives inserts/updates/deletes and returns confirmation changes with new version numbers and a new change token.
2. Update cloud cache with the returned changeset.
3. Upload knowledge files (file attachments) to blob storage, max 5 parallel.
4. Write updated workspace files and change token.

Workflows are pushed separately via `UpsertWorkflowForAgentAsync`, which reads each `workflows/{name}-{guid}/` folder and calls `UpdateWorkflowAsync` per workflow.

### Verify push

Entry point: `VerifyPushAsync`.

1. Read the pushed workspace definition.
2. Clone the agent to a temporary directory to get the server's current state.
3. Compute local changes between the pushed definition and the server clone.
4. If no differences: fully accepted. Otherwise: group differences by change kind and report per-entity-type acceptance.

This detects silent push rejection — the server reports success but does not persist the change.

---

## Hidden state (`.mcs/`)

The `.mcs/` directory is the sync system's internal bookkeeping. It is gitignored (`.mcs/.gitignore` contains `*`).

**`conn.json`** — `AgentSyncInfo`: Dataverse endpoint URL, environment ID, account info, agent or component collection ID, solution versions, and (optionally) the Island Control Plane management endpoint. Written once on clone; read on every sync operation.

**`botdefinition.json`** — Cloud cache: the full `DefinitionBase` (including all components, flows, connection references, environment variables) serialized as JSON at the last sync point. Used as the "original" in 3-way merge and as the baseline for local change detection. Updated after every clone, push, and pull.

**`changetoken.txt`** — Opaque delta sync token from the Island Control Plane. Passed to `GetComponentsAsync` to receive only components that changed since the token was issued. A null token requests the full component set (used on clone).

**Legacy migration**: If `botdefinition.json` does not exist but `botdefinition.yml` does (old YAML format), `ReadCloudCacheSnapshot` reads the YAML file instead. New writes always use JSON.

---

## Reusable and managed components

Components from external managed solutions are excluded from workspace file writing. The filter (`IsReusableOrNonCustomizableComponent`) checks two conditions:

1. **Reuse policy**: `ShareContext.ReusePolicy` is `Private` or `Public` (not `Unknown`).
2. **Managed and non-customizable**: `ManagedProperties.IsManaged == true && IsCustomizable == false`.

These components remain in the cloud cache and in the `DefinitionBase` for ID tracking, but no `.mcs.yml` file is written. This prevents emitting content that the user should not edit.

---

## Change detection

Local change detection (`GetLocalChanges`) compares workspace files to the cloud cache:

- **Structural comparison**: `RootElement.Equals(other, NodeComparison.Structural)` after stripping `mcs.metadata`. This ignores formatting differences and YAML comment changes.
- **Metadata comparison**: `DisplayName` and `Description` are compared separately (normalized: trimmed, quotes stripped, line endings unified).
- **Insert**: component exists locally but not in cloud cache.
- **Update**: component exists in both but differs structurally or in metadata.
- **Delete**: component exists in cloud cache but not locally (file was deleted).

File attachment components are excluded from delete detection — deleting a `.mcs.yml` metadata file does not trigger a server-side delete of the knowledge file.

---

## Pac parity gaps

Two confirmed divergences where the shared library (ported from the extension) provides capabilities that pac does not:

1. **Component collections**: The library clones collection contents into sibling workspaces with cross-references. Pac records only the collection schema name.

2. **Environment variable definitions**: The library projects agent-scoped env vars as `environmentvariables/*.mcs.yml`. Pac does not project them.

These are not defects — they are feature gaps in pac that the shared library already handles correctly.
