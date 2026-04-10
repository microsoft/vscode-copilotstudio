# Sync Semantics Reference

> Definitive reference for how each entity type behaves on clone, push, and pull
> in the `CopilotStudio.Sync` shared library.
>
> Source of truth: QA boundary map (`content-type-coverage.md`, 19 entity types),
> per-entity-type live test findings (`d2b-findings.md`), and the library source
> code in this directory.

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

## Workspace layout

After a clone, the workspace looks like this:

```
agent.mcs.yml                          ← agent metadata (GptComponentMetadata)
settings.mcs.yml                       ← agent settings (BotEntity)
icon.png                               ← agent icon (extracted from BotEntity.IconBase64)
connectionreferences.mcs.yml           ← portable connection references
references.mcs.yml                     ← component collection schema name references
topics/
  {name}.mcs.yml                       ← adaptive dialogs (topics)
actions/
  {name}.mcs.yml                       ← task dialogs (cloud flows, connectors, AI models, connected agents)
agents/
  {agentName}/
    agent.mcs.yml                      ← sub-agent dialog
knowledge/
  {name}.mcs.yml                       ← knowledge sources and configurations
  files/
    {name}.mcs.yml                     ← file attachment metadata
variables/
  {name}.mcs.yml                       ← global variables (if server emits standalone components)
settings/
  {name}.mcs.yml                       ← bot settings components
entities/
  {name}.mcs.yml                       ← custom entities
skills/
  {name}.mcs.yml                       ← BotFramework skills
trigger/
  {name}.mcs.yml                       ← external trigger configurations
translations/
  {name}.mcs.yml                       ← translation/localization components
environmentvariables/
  {schemaName}.mcs.yml                 ← agent-scoped environment variable definitions
workflows/
  {name}-{guid}/
    metadata.yml                       ← workflow metadata
    workflow.json                       ← Logic Apps workflow definition (JSON)
.mcs/                                  ← hidden state (gitignored)
  conn.json                            ← AgentSyncInfo (Dataverse URL, environment ID, account)
  botdefinition.json                   ← cloud cache — full server definition at last sync
  changetoken.txt                      ← opaque delta sync token from Island Control Plane
  .gitignore                           ← contains "*" — entire directory is hidden from git
```

Component collections get a separate sibling workspace:

```
{CollectionName}/
  collection.mcs.yml                   ← BotComponentCollection metadata
  topics/                              ← collection's own topics
  workflows/                           ← collection's own workflows
  ...                                  ← same layout as agent workspace
```

The agent workspace's `references.mcs.yml` contains relative paths to sibling
collection workspaces. These paths are filled in during a two-pass clone
(`CloneChangesAsync` → `ApplyTouchupsAsync`).

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

## Entity type reference

### Summary table

| # | Entity type | Disk path | Clone | Push | Pull |
|---|---|---|---|---|---|
| 1 | Topics (AdaptiveDialog) | `topics/*.mcs.yml` | projected | accepted | 3-way merge |
| 2 | Actions (TaskDialog) | `actions/*.mcs.yml` | projected | accepted | 3-way merge |
| 3 | Sub-agents (AgentDialog) | `agents/{name}/agent.mcs.yml` | projected | accepted | 3-way merge |
| 4 | Agent metadata (GptComponentMetadata) | `agent.mcs.yml` | projected | accepted | 3-way merge |
| 5 | Agent settings (BotEntity) | `settings.mcs.yml` | projected | server silently rejects | 3-way merge on settings props |
| 6 | Icon | `icon.png` | extracted from BotEntity.IconBase64 | inconclusive | via BotEntity pull |
| 7 | Knowledge sources | `knowledge/*.mcs.yml` | projected | server silently rejects | 3-way merge |
| 8 | File attachments | `knowledge/files/*.mcs.yml` | projected + blob download | blob upload | blob download |
| 9 | Global variables | `variables/*.mcs.yml` | projected if server emits | not tested | 3-way merge |
| 10 | Environment variable definitions | `environmentvariables/*.mcs.yml` | projected (agent-scoped) | not tested | via changeset |
| 11 | Cloud flows (workflows) | `actions/*.mcs.yml` + `workflows/*/` | projected + Dataverse download | Dataverse upsert | Dataverse download |
| 12 | Connection references | `connectionreferences.mcs.yml` | projected | clone-fidelity | written from definition |
| 13 | Component collections | `references.mcs.yml` + sibling workspace | projected (2-pass) | not tested | not tested |
| 14 | Connector definitions | `actions/*.mcs.yml` | projected (as TaskDialog) | accepted | 3-way merge |
| 15 | AI model definitions | `actions/*.mcs.yml` | projected (as TaskDialog) | accepted | 3-way merge |
| 16 | AI plugin operations | `actions/*.mcs.yml` | projected (as TaskDialog) | not tested | 3-way merge |
| 17 | Connected agents | `actions/*.mcs.yml` | projected (as TaskDialog) | accepted | 3-way merge |
| 18 | Skills (BotFramework) | `skills/*.mcs.yml` | projected | server silently rejects | 3-way merge |
| 19 | Dataverse table search | not projected (in cloud cache only) | in `.mcs/botdefinition.json` | N/A | N/A |

---

### 1. Topics (AdaptiveDialog)

**Projection rule**: infix `.topic.`, folder `topics/`, DotPassthrough enabled.

**What it is**: Conversational topics — the primary authored content in a Copilot Studio agent. Each topic is a dialog tree with trigger phrases, message nodes, condition branches, and action steps.

**Clone**: Island CP returns `DialogComponent` entries with `kind: AdaptiveDialog`. The projection rule strips the bot schema prefix and `.topic.` infix to derive the filename. Written as `topics/{shortName}.mcs.yml` via `CodeSerializer.SerializeAsMcsYml`.

**Push**: Local changes detected by structural comparison of `RootElement` (ignoring `mcs.metadata`). The changeset includes inserts, updates, and deletes. Topics are the one entity type with confirmed round-trip push success — edits persist server-side.

**Pull**: 3-way merge when both local and remote have changed the same topic. YAML content merged structurally; DisplayName/Description merged with remote-wins-on-conflict semantics.

---

### 2. Actions (TaskDialog)

**Projection rule**: infix `.action.`, folder `actions/`, DotPassthrough enabled.

**What it is**: The `actions/` folder is a polymorphic container. A `TaskDialog` wraps different action kinds: `InvokeFlowTaskAction` (cloud flows), `InvokeConnectorTaskAction` (custom connectors), `InvokeAIBuilderModelTaskAction` (AI prompts), `InvokeConnectedAgentTaskAction` (connected agents), and others.

**Clone/Push/Pull**: Same mechanics as topics. The action kind determines what the wrapper references, but the sync system treats all TaskDialogs uniformly through the component projection pipeline.

The action wrapper itself is a `DialogComponent` in `botdefinition.json`. The referenced external entity (workflow definition, connector definition, AI model definition) lives in a separate top-level array in the bot definition and may or may not be projected to disk — see the individual entity type sections below.

---

### 3. Sub-agents (AgentDialog)

**Projection rule**: infix `.agent.`, folder `agents/`, DotPassthrough disabled.

**What it is**: A dialog representing a sub-agent within a multi-agent workspace. Gets its own subdirectory.

**Clone**: Written to `agents/{agentName}/agent.mcs.yml`. The schema name is derived as `{botName}.agent.{agentName}`.

**Push/Pull**: Same component pipeline as topics.

---

### 4. Agent metadata (GptComponentMetadata / GptComponent)

**Projection rule**: infix `.gpt.`, no folder (root), DotPassthrough disabled.

**What it is**: The top-level agent configuration — instructions, orchestration settings, model selection. Always written to `agent.mcs.yml` at the workspace root.

**Clone**: If the changeset includes a `GptComponent`, it is written to `agent.mcs.yml`. If no `GptComponentMetadata` exists (brand-new agent), a default empty one is written so the file always exists after clone.

**Push**: Instructions edits are accepted and persist server-side.

**Pull**: 3-way merge. Schema name is always `{botName}.gpt.default`.

---

### 5. Agent settings (BotEntity)

**Disk path**: `settings.mcs.yml` (root).

**What it is**: The `BotEntity` — the agent's identity record containing display name, description, schema name, language, and other metadata. Written via `WriteBotEntityAsync` which serializes only the settings-relevant YAML properties (`WithOnlySettingsYamlProperties`).

**Clone**: Written from `changeset.Bot ?? bot.Entity`. The `IconBase64` property is extracted and written as a separate `icon.png` file.

**Push**: Push reports success but the server silently rejects settings changes. Verified empirically: description edits do not persist. This is server behavior, not a library defect.

**Pull**: 3-way merge on settings YAML properties. Non-settings properties (including `IconBase64`) are restored from the remote entity after merge. `FilterUnchangedComponents` compares both settings properties and `IconBase64` to decide whether to skip the write.

---

### 6. Icon

**Disk path**: `icon.png` (root).

**What it is**: The agent's icon, stored as `BotEntity.IconBase64` in the server payload. Extracted to a binary PNG file on disk.

**Clone**: `WriteBotEntityAsync` decodes `IconBase64` and writes `icon.png`.

**Push**: Inconclusive — a 1x1 PNG replacement was likely rejected by server size validation. Not retested with a properly-sized icon.

**Pull**: The icon is updated through the BotEntity pull path. When the remote entity has a different `IconBase64`, `FilterUnchangedComponents` detects the difference and preserves the entity in the changeset, causing `WriteBotEntityAsync` to write the new icon.

---

### 7. Knowledge sources (KnowledgeSource / KnowledgeSourceConfiguration / KnowledgeSourceComponent)

**Projection rule**: infix `.knowledge.`, folder `knowledge/`, DotPassthrough enabled.

**What it is**: Knowledge sources provide the agent with information from websites, Dataverse tables, or uploaded files. Three ObjectModel types (`KnowledgeSource`, `KnowledgeSourceConfiguration`, `KnowledgeSourceComponent`) all project to the same folder. `NormalizeElement` promotes `KnowledgeSource` to `KnowledgeSourceConfiguration` for consistent handling.

**Clone**: Projected to `knowledge/{shortName}.mcs.yml`. Variants observed: website sources, file sources, and `DataverseStructuredSearchSource` (from Dataverse table knowledge).

**Push**: Push reports success but description edits are not persisted server-side. Same silent rejection pattern as agent settings.

**Pull**: 3-way merge on YAML content.

---

### 8. File attachments (FileAttachmentComponent / FileAttachmentComponentMetadata)

**Projection rule**: infix `.file.`, folder `knowledge/files/`, DotPassthrough enabled.

**What it is**: Metadata for uploaded knowledge files (PDFs, documents). The actual file content lives in blob storage, not in the component YAML.

**Clone**: Metadata projected to `knowledge/files/{name}.mcs.yml`. The actual file is downloaded from blob storage via `DownloadKnowledgeFileAsync`.

**Push**: New file attachments are uploaded to blob storage via `UploadKnowledgeFileAsync` (max 5 parallel, max 125 MB per file). The metadata component is pushed through the standard changeset.

**Pull**: Blob files are re-downloaded during pull. File attachment changes are excluded from conflict detection (`ChangeKind != FileAttachmentComponent`).

**ReadWorkspaceDefinition**: Detects new binary files in `knowledge/files/` that don't have corresponding `.mcs.yml` metadata. Generates a schema name and creates a `FileAttachmentComponent` for upload on next push.

---

### 9. Global variables (Variable / GlobalVariableComponent)

**Projection rule**: infix `.GlobalVariableComponent.`, folder `variables/`, DotPassthrough disabled.

**What it is**: Agent-scoped global variables. The projector and round-trip tests exist in the extension codebase (`ProjectorOracleParityTests.GlobalVariableComponent_RoundTrip`), and the test fixtures use `scope: User` variables.

**Live testing (one case)**: Created one global variable (`GlobalTestVar`, scope: Global, type: String, draft-only) in UsefulTestAgent. The Island CP's component set contained only `DialogComponent` and `GptComponent` kinds — no `GlobalVariableComponent` appeared. The variable was present only as an inline `variable: Global.GlobalTestVar` reference in the topic YAML. Both pac clone and extension pull produced identical results: no `variables/` directory.

Whether the server emits `GlobalVariableComponent` under other conditions (different scope, type, publish state, or API version) is untested. The extension fixture uses `scope: User` variables, which may represent either a planned server capability or a prior API version's behavior.

**Clone**: When `GlobalVariableComponent` entries are present in the component set, they project to `variables/{name}.mcs.yml`. In the one live test case, no such entries were returned.

**Push/Pull**: Standard component pipeline. Would work if the component type appeared in the changeset; not exercised in live testing.

---

### 10. Environment variable definitions

**Disk path**: `environmentvariables/{schemaName}.mcs.yml`.

**What it is**: Agent-scoped environment variable definitions — variables with the agent's schema name prefix (e.g., `cre4a_agentza-S19.skill.CopilotStudioEchoSkill.AppId`). Typically auto-created by skill infrastructure.

**Clone**: `WriteEnvironmentVariables` iterates `definition.EnvironmentVariables` and writes each as YAML. All environment variables from the server payload are projected, but only agent-scoped ones are meaningful (environment-level vars are included in the payload regardless of scoping).

**Push**: `ReadEnvironmentVariablesAsync` reads `environmentvariables/*.mcs.yml` files back into the definition. Metadata (Id) is restored from the cloud cache. Not tested for round-trip persistence.

**Pull**: Incremental changes arrive via `changeset.EnvironmentVariableChanges` (upserts and deletes). On full clone (no individual changes), all env vars are written.

**Pac parity gap**: pac does not project environment variable definitions. They exist only in `.mcs/botdefinition.json`. This is a confirmed divergence.

---

### 11. Cloud flows (workflows)

**Disk path**: `actions/{name}.mcs.yml` (action wrapper) + `workflows/{name}-{guid}/metadata.yml` + `workflow.json`.

**What it is**: Power Automate cloud flows associated with the agent. The action wrapper is a `TaskDialog` with `kind: InvokeFlowTaskAction` containing the flow ID. The actual flow definition is a Logic Apps JSON document.

**Clone**: The action wrapper is projected through the standard component pipeline. Workflows are fetched separately from Dataverse via `DownloadAllWorkflowsForAgentAsync`, written as `metadata.yml` (YAML, camelCase) and `workflow.json` (pretty-printed JSON). The workflow folder name is `{sanitizedName}-{workflowId}`.

**Push**: Action wrapper pushed through standard changeset. Workflow definition pushed via `UpsertWorkflowForAgentAsync` → `UpdateWorkflowAsync` per workflow. Connection references extracted from flow definitions are resolved separately.

**Pull**: Workflows are re-downloaded from Dataverse at the start of pull. If a remote workflow is deleted, its local folder is removed. Workflow change detection compares local `workflow.json` content against remote.

---

### 12. Connection references

**Disk path**: `connectionreferences.mcs.yml` (root).

**What it is**: A portable file listing connection references needed by the agent. Used for provisioning connections in a new environment.

**Clone**: `WriteConnectionReferencesAsync` serializes `definition.ConnectionReferences` if any exist. Connection references come from two sources: the Island CP component set (for agent components) and workflow metadata (for cloud flow connections).

**Push**: Not pushed through the changeset. Connection references are server-managed. `ProvisionConnectionReferencesAsync` can create missing connection references via Dataverse API.

**Pull**: Rewritten from the updated definition after each pull. Clone-fidelity verified: repeated clones produce identical files.

---

### 13. Component collections

**Disk path**: `references.mcs.yml` (agent workspace) + sibling `{CollectionName}/` workspace.

**What it is**: Reusable component collections shared across agents. An agent references collections by schema name; the collection itself is a separate workspace with its own `collection.mcs.yml` and component files.

**Clone (multi-asset)**: `CloneAllAssetsAsync` iterates agent + collection operation contexts. Each gets its own subfolder. The agent workspace's `references.mcs.yml` initially records only the collection's schema name. The second pass (`ApplyTouchupsAsync`) fills in relative directory paths to sibling workspaces.

**Clone (agent-only)**: `references.mcs.yml` records the collection schema name without a directory path — identical to pac clone behavior. Components that belong to a collection (identified by `ParentBotComponentCollectionId`) are skipped during workspace file writing.

**Pac parity gap**: pac clone only records the schema name reference; it does not clone collection contents. The extension's multi-asset clone with sibling workspaces is a capability pac does not have.

---

### 14. Connector definitions

**Disk path**: `actions/{name}.mcs.yml` (as TaskDialog with `InvokeConnectorTaskAction`).

**What it is**: Custom connectors added to the agent. The action wrapper references the connector; the `connectorDefinitions` array in `botdefinition.json` holds the full definition but is not projected as a standalone file.

**Clone**: Action wrapper projected to `actions/`. The connector's connection reference appears in `connectionreferences.mcs.yml`.

**Push**: Action wrapper push accepted and persists server-side.

**Pull**: Standard 3-way merge on the action wrapper.

---

### 15. AI model definitions

**Disk path**: `actions/{name}.mcs.yml` (as TaskDialog with `InvokeAIBuilderModelTaskAction`).

**What it is**: AI Builder prompts. The action wrapper references the AI model by ID; the `aIModelDefinitions` array in `botdefinition.json` holds the full definition but is not projected as a standalone file.

**Clone**: Action wrapper projected to `actions/`.

**Push**: Accepted and persists server-side.

**Pull**: Standard 3-way merge.

---

### 16. AI plugin operations

**Disk path**: `actions/{name}.mcs.yml` (as TaskDialog).

**What it is**: AI plugin actions (e.g., GitHub SearchIssues). Projected identically to other TaskDialogs.

**Clone**: Projected. No divergence between pac and extension.

**Push**: Not tested.

**Pull**: Standard 3-way merge.

---

### 17. Connected agents

**Disk path**: `actions/{name}.mcs.yml` (as TaskDialog with `InvokeConnectedAgentTaskAction`).

**What it is**: References to other published agents that this agent can invoke. The action wrapper includes `botSchemaName` pointing to the connected agent. This is the modern "Connected agents" feature, distinct from the legacy "Connected bots" schema collection (which is not exercised and may be obsolete).

**Clone**: Action wrapper projected to `actions/`. The schema name includes the full `InvokeConnectedAgentTaskAction` qualifier.

**Push**: Accepted and persists server-side.

**Pull**: Standard 3-way merge.

---

### 18. Skills (BotFramework)

**Projection rule**: infix `.skill.`, folder `skills/`, DotPassthrough enabled.

**What it is**: BotFramework skills (Agent SDK). The skill component references agent-scoped environment variables by schema name for `appId` and `appEndpoint`. Distinct from connected agents.

**Clone**: Projected to `skills/{name}.mcs.yml` with `kind: SkillDefinition`.

**Push**: Push reports success but the skill edit is not persisted server-side. In batch pushes with other entity types, a crash has been observed (`System.ArgumentException: Improper response, not implemented`). The library's `ComponentWriter` uses `TryGetBotComponentById` (not `VerifiedGet`) to gracefully handle skills that fail round-trip through `ApplyChanges`.

**Pull**: Standard 3-way merge.

---

### 19. Dataverse table search (and related configurations)

**Disk path**: none — present only in `.mcs/botdefinition.json`.

**What it is**: Four related schema collections — `DataverseTableSearchs`, `DataverseTableSearchEntityConfigurations`, `DataverseTableSearchGlossaryConfigurations`, `DataverseTableSearchEntityColumnSynonyms`. These configure Dataverse search for knowledge retrieval.

**Clone**: Data is included in the `botdefinition.json` cloud cache but is not projected to workspace files. No projection rule exists for these types. The Dataverse table *knowledge source* that references the search configuration is projected (see entity type 7).

**Push/Pull**: Not applicable — no workspace files to detect changes in.

---

## Additional entity types in projection rules

The following types have projection rules in `LspProjection.cs` but are present only in extension test fixtures, not in live server responses as of current testing:

| Type | Folder | Status |
|---|---|---|
| `BotSettingsBase` / `BotSettingsComponent` | `settings/` | Fixture only; live pulls produce no subdirectory |
| `Entity` / `EntityWithAnnotatedSamples` / `CustomEntityComponent` | `entities/` | Fixture only |
| `ExternalTriggerConfiguration` / `ExternalTriggerComponent` | `trigger/` | Fixture only |
| `TranslationsComponent` / `LocalizableContentContainer` | `translations/` | Fixture only; detected by `IsTranslationSchemaName` (locale suffix pattern `xx-xx`) |

The projectors are implemented and tested. If the server begins emitting these component types, they will project correctly without code changes.

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
