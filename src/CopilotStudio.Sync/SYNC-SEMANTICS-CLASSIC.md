# Sync Semantics Reference — Classic layout

> Disk layout and per-entity-type reference for **classic** agents
> (`AuthoringShape.Classic`) — the historical layout, kept byte-identical.
>
> CLI-layered agents use a different on-disk layout: see
> [SYNC-SEMANTICS-CLI.md](./SYNC-SEMANTICS-CLI.md). The shape-neutral sync
> mechanics (operations pipeline, hidden state, reusable/managed filtering,
> change detection, pac parity) live in
> [SYNC-SEMANTICS.md](./SYNC-SEMANTICS.md).

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
  {name}.mcs.yml                       ← task dialogs (cloud flows, connectors, AI models)
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
| 17 | Connected agents | `agents/*.mcs.yml` | projected (as TaskDialog) | accepted | 3-way merge |
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

**What it is**: The `actions/` folder is a polymorphic container. A `TaskDialog` wraps different action kinds: `InvokeFlowTaskAction` (cloud flows), `InvokeConnectorTaskAction` (custom connectors), `InvokeAIBuilderModelTaskAction` (AI prompts), `InvokeConnectedAgentTaskAction` (connected agents — but routed to `agents/`, not `actions/`; see entity type 17), and others.

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

**Projection rule**: infix `.globalvariable.`, folder `variables/`, DotPassthrough disabled.

**What it is**: Agent-scoped global variables. The projector and round-trip tests exist in the extension codebase (`ProjectorOracleParityTests.GlobalVariableComponent_RoundTrip`), and the test fixtures use `scope: User` variables.

**Live testing (one case)**: Created one global variable (`GlobalTestVar`, scope: Global, type: String, draft-only) in UsefulTestAgent. The Island CP's component set contained only `DialogComponent` and `GptComponent` kinds — no `GlobalVariableComponent` appeared. The variable was present only as an inline `variable: Global.GlobalTestVar` reference in the topic YAML. Both pac clone and extension pull produced identical results: no `variables/` directory.

Whether the server emits `GlobalVariableComponent` under other conditions (different scope, type, publish state, or API version) is untested. The extension fixture uses `scope: User` variables, which may represent either a planned server capability or a prior API version's behavior.

**Clone**: When `GlobalVariableComponent` entries are present in the component set, they project to `variables/{name}.mcs.yml`. In the one live test case, no such entries were returned.

**Push/Pull**: Standard component pipeline. Would work if the component type appeared in the changeset; not exercised in live testing.

---

### 10. Environment variable definitions

**Disk path**: `environmentvariables/{schemaName}.mcs.yml`.

**What it is**: Agent-scoped environment variable definitions — variables with the agent's schema name prefix (e.g., `cre4a_agentza-S19.skill.CopilotStudioEchoSkill.AppId`). Typically auto-created by skill infrastructure.

**Clone**: `WriteEnvironmentVariables` iterates `definition.EnvironmentVariables`, but only **agent-scoped** definitions are projected: `WriteEnvironmentVariable` writes a file only when the definition's schema-name prefix matches the agent's schema name (`GetAgentSchemaName(...) == agentSchemaName`). Environment-level variables present in the payload are filtered out, not written.

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

**Disk path**: `agents/{name}.mcs.yml` (a `TaskDialog` with `InvokeConnectedAgentTaskAction`, routed to `agents/` by a projection RuleOverride keyed on the `.InvokeConnectedAgentTaskAction.` schema infix — *not* `actions/`).

**What it is**: References to other published agents that this agent can invoke. The action wrapper includes `botSchemaName` pointing to the connected agent. This is the modern "Connected agents" feature, distinct from the legacy "Connected bots" schema collection (which is not exercised and may be obsolete).

**Clone**: Action wrapper projected to `agents/`. The schema name includes the full `InvokeConnectedAgentTaskAction` qualifier (which triggers the `agents/` RuleOverride).

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
