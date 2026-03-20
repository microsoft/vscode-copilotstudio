# LSP Journal CLI — Test Matrix

Mutable coverage tracker. Update cells as journals are added or expanded.

**Lenses** (columns) = the LSP behaviors being tested:

| Lens | Meaning |
|------|---------|
| **Fix** | Fixture file exists for this type |
| **Idx** | Workspace indexing discovers and classifies the file |
| **Cmp** | Completions return meaningful items at relevant positions |
| **Diag** | Diagnostics fire for error states (or confirmed absent for valid files) |
| **Proj** | File projection resolves schema-name ↔ file-path correctly |

**Cell values:** `✅` covered, `—` not applicable, blank = gap.

---

## Solution-Level Components

| Component | Fix | Idx | Cmp | Diag | Proj | Notes |
|-----------|-----|-----|-----|------|------|-------|
| `BotEntity` (settings.mcs.yml) | ✅ | ✅ | ✅ | | | |
| `GptComponent` (agent.mcs.yml) | ✅ | ✅ | ✅ | | | GPT special-case projection untested |
| `DialogComponent` (topics/, actions/) | ✅ | ✅ | ✅ | ✅ | ✅ | Core type; best covered |
| `CustomEntityComponent` (entities/) | ✅ | ✅ | ✅ | ✅ | | InvalidEntity has suspect S5 |
| `GlobalVariableComponent` (variables/) | ✅ | ✅ | ✅ | | | |
| `SkillComponent` (skills/) | ✅ | ✅ | ✅ | | | |
| `FileAttachmentComponent` (knowledge/files/) | ✅ | ✅ | ✅ | | | |
| `KnowledgeSourceComponent` (knowledge/) | ✅ | ✅ | ✅ | | | |
| `ExternalTriggerComponent` (trigger/) | ✅ | ✅ | ✅ | | | |
| `BotSettingsComponent` (settings/) | ✅ | ✅ | ✅ | | | |
| `TranslationsComponent` (translations/) | ✅ | ✅ | ✅ | | | |
| `ConnectionReference` (connectionreferences) | ✅ | ✅ | ✅ | | | |
| `TestCaseComponent` | | | | | | No fixture |
| `CloudFlowDefinition` | | | | | | No fixture |
| `EnvironmentVariableDefinition` | | | | | | No fixture |
| `ConnectorDefinition` | | | | | | No fixture |
| `AIPluginOperationComponent` | | | | | | No fixture |
| `AIModelDefinition` | | | | | | No fixture |
| `ConnectedAgentDefinition` | | | | | | No fixture |
| `BotComponentCollection` | | | | | | No fixture |
| `DataverseTableSearch` | | | | | | No fixture |

### Source-File-Only

| Component | Fix | Idx | Cmp | Diag | Proj | Notes |
|-----------|-----|-----|-----|------|------|-------|
| `ConnectionReferencesSourceFile` | ✅ | ✅ | ✅ | | | |
| `ReferencesSourceFile` (references.mcs.yml) | | | | | | Needed for attached-mode |

---

## Dialog Types

| Kind | Fix | Idx | Cmp | Diag | Proj | Notes |
|------|-----|-----|-----|------|------|-------|
| `AdaptiveDialog` | ✅ (8) | ✅ | ✅ | ✅ | ✅ | Core type |
| `TaskDialog` | ✅ (4) | ✅ | ✅ | | ✅ | |
| `AgentDialog` | ✅ (1) | ✅ | ✅ | | | Server crashes on `kind: Agent` — blocks Proj |

---

## Trigger Types (in AdaptiveDialog)

| Kind | Fix | Idx | Cmp | Notes |
|------|-----|-----|-----|-------|
| `OnRecognizedIntent` | ✅ | ✅ | ✅ | 5 files |
| `OnConversationStart` | ✅ | ✅ | ✅ | |
| `OnUnknownIntent` | ✅ | ✅ | ✅ | |
| `OnToolSelected` | ✅ | ✅ | ✅ | |
| `OnRedirect` | ✅ | ✅ | ✅ | |
| `OnActivity` | ✅ | ✅ | ✅ | |
| `OnError` | ✅ | ✅ | ✅ | |
| `OnPlanComplete` | | | | |
| `OnKnowledgeRequested` | | | | |
| `OnGeneratedResponse` | | | | |
| `OnOutgoingMessage` | | | | |
| `OnCopilotRedirect` | | | | |
| `OnEventActivity` | | | | |
| `OnInactivity` | | | | |
| `OnSignIn` | | | | |
| `OnSelectIntent` | | | | |
| `OnAnalyzeText` | | | | |
| `RecurrenceTrigger` | | | | |
| `CreateConversationConnectorTrigger` | | | | |
| `ContinueConversationConnectorTrigger` | | | | |

---

## TaskAction Types (in TaskDialog)

| Kind | Fix | Idx | Cmp | Notes |
|------|-----|-----|-----|-------|
| `InvokeConnectorTaskAction` | ✅ | ✅ | ✅ | actions/GetWeather |
| `InvokeConnectedAgentTaskAction` | ✅ | ✅ | ✅ | actions/CallSubAgent |
| `InvokeFlowTaskAction` | ✅ | ✅ | ✅ | actions/RunComplianceFlow |
| `InvokeSkillTaskAction` | | | | |
| `InvokeAIPluginTaskAction` | | | | |
| `InvokeAIBuilderModelTaskAction` | | | | |
| `InvokeClientTaskAction` | | | | |
| `CustomTool` | | | | |
| `CodeInterpreterTool` | | | | |
| `WebSearchTool` | | | | |
| `FileSearchTool` | | | | |
| `McpServerTool` | | | | |
| `OpenApiTool` | | | | |

---

## In-Dialog Action Types (in `actions:` lists)

| Kind | Fix | Cmp | Notes |
|------|-----|-----|-------|
| `SendActivity` | ✅ | ✅ | Multiple files |
| `CancelAllDialogs` | ✅ | ✅ | |
| `SetVariable` | ✅ | ✅ | |
| `IfCondition` | ✅ | ✅ | |
| `BeginDialog` | ✅ | ✅ | |
| `EmitEvent` | ✅ | ✅ | |
| `EndDialog` | ✅ | ✅ | |
| `ConditionGroup` | ✅ | ✅ | |
| `Foreach` | ✅ | ✅ | |
| `OAuthInput` | ✅ | ✅ | |
| `GotoAction` | | | |
| `DisableTrigger` | | | |
| `ClearAllVariables` | | | |
| `BreakLoop` | | | |
| `ContinueLoop` | | | |
| `ResetVariable` | | | |
| `EditTable` / `EditTableV2` | | | |
| `HttpRequest` | | | |
| `CSATQuestion` | | | |

---

## Entity Types

| Kind | Fix | Idx | Cmp | Diag | Notes |
|------|-----|-----|-----|------|-------|
| `ClosedListEntity` | ✅ | ✅ | ✅ | ✅ | InvalidEntity → suspect S5 |
| `RegexEntity` | ✅ | ✅ | ✅ | | |
| `ExternalEntity` | | | | | |

---

## Knowledge Source Types

| Kind | Fix | Idx | Cmp | Notes |
|------|-----|-----|-----|-------|
| `PublicSiteSearchSource` | ✅ | ✅ | ✅ | |
| `SharePointSearchSource` | ✅ | ✅ | ✅ | |
| `FileGroupKnowledgeSource` | | | | |
| `CustomKnowledgeSource` | | | | |
| `GraphConnectorSearchSource` | | | | |
| `AzureOpenAIOnYourDataKnowledgeSource` | | | | |
| `AzureAISearchSource` | | | | |
| `BingCustomSearchSource` | | | | |
| `DataverseStructuredSearchSource` | | | | |
| `FederatedStructuredSearchSource` | | | | |
| `FabricAISkillSource` | | | | |
| `TeamsMessageSearchSource` | | | | |
| `MeetingSearchSource` | | | | |
| `EmailSearchSource` | | | | |

---

## External Trigger Types

| Kind | Fix | Idx | Cmp | Notes |
|------|-----|-----|-----|-------|
| `ConnectorTriggerDefinition` | ✅ | ✅ | ✅ | |
| `RecurrenceTriggerDefinition` | | | | |
| `WorkflowExternalTrigger` | | | | |

---

## Bot Settings Types

| Kind | Fix | Idx | Cmp | Notes |
|------|-----|-----|-----|-------|
| `ContentModerationSettings` | ✅ | ✅ | ✅ | |
| `DefaultFeedbackCollection` | ✅ | ✅ | ✅ | |
| `AutonomousSettings` | ✅ | ✅ | ✅ | |
| `IvrBotSettings` | | | | |
| `RealTimeVoiceBotSettings` | | | | |

---

## Completion Depth (cross-cutting)

Beyond per-type surface completions, these behaviors are distinct completion
code paths that need dedicated coverage:

| Behavior | Covered | Journal | Notes |
|----------|---------|---------|-------|
| Property-key completion (cursor in `actions:` block) | | | |
| Enum-value completion (`kind: ` cleared then completed) | | | |
| Trigger char `:` | | | |
| Trigger char `\n` | | | |
| Trigger char ` ` (space) | | | |
| Trigger char `.` | | | |
| Completion in error-state file | | | |
| PropertyPath snippet | | | |
| InitializablePropertyPath snippet | | | |

## Workspace Indexing Edge Cases

| Behavior | Covered | Journal | Notes |
|----------|---------|---------|-------|
| Empty component directory (variables/) | ✅ | workspace-indexing-edges | Init succeeds; indexer handles empty folders |
| Unexpected file extensions (.txt, .md in component folders) | ✅ | workspace-indexing-edges | Fixture has stray-notes.txt, readme.md; indexer ignores them |
| Folder→element-type: topics/ → AdaptiveDialog | ✅ | workspace-indexing-edges | Diagnostics confirm correct type handling |
| Folder→element-type: actions/ → TaskDialog | ✅ | workspace-indexing-edges | Diagnostics confirm correct type handling |
| Folder→element-type: entities/ → ClosedListEntity | ✅ | workspace-indexing-edges | Diagnostics confirm correct type handling |
| Type→file-candidate resolution via completion | ✅ | workspace-indexing-edges | Completions on topic resolve workspace files |
| Non-standard folder in workspace (unknown-folder/) | ✅ | workspace-indexing-edges | Fixture present; indexer doesn't crash on init. Opening as doc crashes session (S6). |

## File Projection Edge Cases

| Behavior | Covered | Journal | Notes |
|----------|---------|---------|-------|
| GPT special case (agent.mcs.yml) | | | |
| Sub-agent folder resolution | | | |
| Dotted display names in filenames | | | Fixture exists but projection untested |
| Qualified schema names | | | |

## Diagnostics: Error-State Coverage

| File | Diag | Suspect | Notes |
|------|------|---------|-------|
| BrokenConnector.mcs.yml | ✅ `[]` | S2 | Connector ref validation doesn't fire in detached mode |
| MissingFields.mcs.yml | ✅ `[]` | S3 | No validation for missing required fields |
| SyntaxError.mcs.yml | ✅ `[]` | S4 | May be fixture content issue |
| InvalidEntity.mcs.yml | ✅ items | S5 | Duplicate ranges for different missing properties |

---

## Summary

| Category | Have fixture | Have Idx | Have Cmp | Have Diag | Have Proj |
|----------|-------------|----------|----------|-----------|-----------|
| Solution components | 12/21 | 12 | 12 | 1 | 1 |
| Dialog types | 3/3 | 3 | 3 | 1 | 2 |
| Trigger types | 7/20 | 7 | 7 | — | — |
| TaskAction types | 3/13 | 3 | 3 | — | — |
| In-dialog actions | 10/19 | — | 10 | — | — |
| Entity types | 2/3 | 2 | 2 | 1 | — |
| Knowledge sources | 2/14 | 2 | 2 | — | — |
| External triggers | 1/3 | 1 | 1 | — | — |
| Bot settings | 3/5 | 3 | 3 | — | — |
| Completion depth | 0/9 | — | — | — | — |
| Projection edge cases | 0/4 | — | — | — | — |
| Indexing edge cases | 7/7 | — | — | — | — |
