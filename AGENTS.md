# AGENTS.md

This file provides guidance to AI coding agents and automation harnesses when working with code in this repository.

## Project Overview

This is the Copilot Studio Extension for Visual Studio Code - a hybrid TypeScript/C# project that provides a full-featured VS Code extension for editing Microsoft Copilot Studio agents. The extension enables developers to clone agents locally, edit components (topics, triggers, actions, knowledge sources) in YAML format, and sync changes bidirectionally with cloud environments. IntelliSense is powered by a Language Server Protocol (LSP) backend written in C#.

**Current State:** Generally Available (GA)

**Documentation:** https://learn.microsoft.com/en-us/microsoft-copilot-studio/visual-studio-code-extension-overview

**Build constraint:** Full .NET restore/build currently depends on internal `Microsoft.Agents.*` NuGet packages. Migration from the internal Dataverse authoring SDK to PAC CLI delegation is in progress to make external builds reproducible.

## Build and Test Commands

### TypeScript / VS Code Extension

The `package.json` is located at `src/vscode-extensions/microsoft-powerplatformlang-extension/package.json`. All npm commands must be run from that directory.

```bash
# Change to the extension directory first
cd src/vscode-extensions/microsoft-powerplatformlang-extension

# Install dependencies
npm install

# The parent src/vscode-extensions/.npmrc sets omit-lockfile-registry-resolved=true
# so lockfile updates omit registry/feed-specific resolved URLs. Do not add
# registry or feed URLs to committed .npmrc or package-lock.json files;
# registry/feed/auth configuration belongs in user npm config or CI restore-time
# configuration.

# Build everything (LSP + type check + esbuild bundle)
npm run compile

# Build only the C# language server for current platform
npm run buildLsp

# Watch LSP for changes (rebuilds on save)
npm run watchLsp

# Type check only (no emit)
npm run check-types

# Lint
npm run lint

# Run VS Code extension tests
npm test

# Package a target-specific pre-release VSIX (default target: win32-x64)
npm run package -- --target win32-x64

# Watch TypeScript compilation
npm run watch
```

### VSIX Packaging

Packaging requires the same internal NuGet restore access as the .NET build. The `npm run package` script publishes the target language server, runs TypeScript type checking, bundles production JavaScript, and invokes `vsce` for the requested VS Code target.

```bash
cd src/vscode-extensions/microsoft-powerplatformlang-extension
npm install
npm run package -- --target win32-x64
```

Supported targets:

| .NET runtime | VS Code target |
|--------------|----------------|
| `win-x64` | `win32-x64` |
| `win-arm64` | `win32-arm64` |
| `linux-x64` | `linux-x64` |
| `linux-arm64` | `linux-arm64` |
| `osx-x64` | `darwin-x64` |
| `osx-x64` | `darwin-arm64` |

```bash
npm run package -- --target <vs-code-target>
```

The package script cleans `lspOut/` before publishing, so repeat `npm run package -- --target <vs-code-target>` for each VSIX target you need. Use `--out <path>` to choose the VSIX output path, `--version <x.y.z>` to stamp the VSIX version (default `0.0.1`), `--configuration Debug` for a Debug LSP publish, or `--no-pre-release` to omit the VSIX pre-release flag (VSIXes are marked pre-release by default).

#### Prompt example: producing a VSIX

Ask the harness in plain language, naming the VS Code target. Example prompts:

> Generate a VSIX for `darwin-arm64`.

> Build a `win32-x64` VSIX at version 1.4.0.

The agent should:

1. Map the requested target to a supported VS Code target in the table above. If the request names something that isn't a valid VS Code target (e.g. `ios-x64`), stop and ask which target was meant rather than guessing.
2. Run the package script from the extension directory, passing `--version` when the user specified one (otherwise the `0.0.1` default applies):

   ```bash
   cd src/vscode-extensions/microsoft-powerplatformlang-extension
   npm run package -- --target darwin-arm64 --version 1.4.0
   ```

3. Report the full path of the produced `.vsix` (written to the extension directory as `vscode-copilotstudio-<target>-<version>.vsix` unless `--out` is given).

The MSBuild `extension.proj` contains the internal multi-platform packaging flow. It publishes self-contained language server binaries for all six targets and then packages each VSIX, but its `VSIXPack` target currently expects `vsce` at `src/vscode-extensions/node_modules/.bin/vsce`; prefer `npm run package -- --target <vs-code-target>` for harness-driven single-target packaging.

### .NET / Language Server

```bash
# Restore, build, and test the same traversal project used by PR CI
dotnet restore src/build.proj
dotnet build src/build.proj --no-restore --configuration Debug -p:langversion=preview
dotnet test src/build.proj --no-build --no-restore --configuration Debug

# Build the language server solution
dotnet build src/LanguageServers/PowerPlatformLS/PowerPlatformLS.sln

# Run all .NET unit tests
dotnet test src/LanguageServers/PowerPlatformLS/PowerPlatformLS.sln

# Run a single test by filter
dotnet test --filter "FullyQualifiedName~Namespace.ClassName.MethodName" src/LanguageServers/PowerPlatformLS/PowerPlatformLS.sln

# Create NuGet packages
dotnet pack --no-build --no-restore -c debug src/LanguageServers/PowerPlatformLS/PowerPlatformLS.sln
```

### LSP Journal Tests

```bash
# Run one journal test
dotnet run --project src/LanguageServers/PowerPlatformLS/Tools/LspJournalCli -- lifecycle

# Run all journal tests
dotnet run --project src/LanguageServers/PowerPlatformLS/Tools/LspJournalCli -- --all

# Accept pending journal changes
dotnet run --project src/LanguageServers/PowerPlatformLS/Tools/LspJournalCli -- accept lifecycle

# List pending journal changes
dotnet run --project src/LanguageServers/PowerPlatformLS/Tools/LspJournalCli -- pending
```

### Development Workflow

1. Open the repo in VS Code
2. Press **F5** to launch the Extension Development Host
3. The extension activates and starts the language server automatically
4. For .NET language server debugging, attach to the `LanguageServerHost` process

### Prerequisites

- .NET 10.0 SDK (specified in `global.json`)
- Node.js 22 LTS
- VS Code 1.108.0+

## Architecture

### High-Level Communication Flow

```
VS Code Extension (TypeScript)
    |
    | JSON-RPC over named pipe (Windows) / Unix socket (macOS/Linux)
    v
LanguageServerHost.exe (C# / .NET 10)
    |
    | DI-based handler routing
    v
Language Implementations + Sync services (MCS / PowerFx / YAML / CopilotStudio.Sync)
    |
    | Pluggable rules
    v
Completions, Diagnostics, Semantic Tokens, Go-to-Definition
```

### TypeScript Extension (`src/vscode-extensions/microsoft-powerplatformlang-extension/`)

**Entry Point:** `client/src/extension.ts`

Initialization sequence:
1. Generate session ID (UUID) for telemetry
2. Initialize logger with session context
3. Register auth commands (signIn, resetAccount, reportIssue) - no LSP dependency
4. Initialize and start LSP client (blocking - all downstream features depend on this)
5. Register LSP-dependent features: tree views, SCM, workspace management, sync commands
6. Run post-open async workflow (e.g., open cloned `.mcs.yml` file)

**Directory Structure:**

| Directory | Purpose |
|-----------|---------|
| `client/src/commands/` | Command handlers (clone, sync, reattach, auth) |
| `client/src/services/` | Core services: LSP client singleton, logger, telemetry |
| `client/src/clone/` | Agent cloning: remote tree view, environment/agent picker, workspace creation |
| `client/src/sync/` | Workspace sync: change tracking, SCM integration, local/remote file providers |
| `client/src/clients/` | External API clients: Dataverse, BAP (Business Application Platform), auth/account |
| `client/src/knowledgeFiles/` | Knowledge source management: virtual filesystem, upload/download, diff |
| `client/src/botComponents/` | Bot component APIs: Dataverse HTTP wrapper, schema name utilities |
| `client/src/generated/` | Generated TypeScript models |
| `client/src/startup/` | Post-activation logic (open cloned file after workspace add) |
| `client/src/tests/` | VS Code host tests |
| `client/src/utils/` | Utilities: URI comparison, cluster lookup |

**Key Services:**

- **`services/lspClient.ts`** - Singleton proxy pattern for the LSP `LanguageClient`. Wraps all LSP requests with telemetry middleware (logs method + response code, throws on non-200). Must initialize before other features load.
- **`services/logger.ts`** - Unified logger with PII redaction (`<pii>text</pii>` → `[REDACTED]` in telemetry). Sends to Application Insights and shows VS Code UI messages.
- **`clients/account.ts`** - Microsoft auth via VS Code's built-in auth provider. Supports cluster-specific token scopes (Prod, GCC High, Mooncake, etc.). Uses coalesced async pattern to prevent concurrent auth dialogs.
- **`clients/dataverseClient.ts`** - OData client for agent listing, solution versions, WhoAmI. Caches per environment, marks 403 failures as permanent.
- **`clients/bapClient.ts`** - Environment discovery by SKU (Developer, Trial, Sandbox, etc.) with progressive loading and cancellation support.

**Command Registration Pattern:**
```typescript
export const registerXCommand = (context: ExtensionContext) => {
  const command = commands.registerCommand('microsoft-copilot-studio.x', async (params?) => {
    // Handle logic
  });
  context.subscriptions.push(command);
};
```

**Tree Views:**

1. **Remote Agents Tree** (`clone/tree.ts`) - Hierarchical browser: SKU sections → Environments (lazy-loaded) → Agents. Uses discriminated unions with type guards for exhaustive pattern matching. Debounced refresh, cached environments per SKU.
2. **Agent Changes Tree** (`sync/agentChangesTreeProvider.ts`) - Three-level hierarchy: Agent → ChangeGroup (Local/Remote) → ChangeItem. Color-coded icons (green=create, blue=update, red=delete). Context values (`changeGroup-local`, `changeGroup-remote`) control menu visibility.
3. **Workspace Agents Tree** (`clone/agentDirectory.ts`) - Shows locally connected agent workspaces.
4. **Remote Knowledge Files Tree** (`knowledgeFiles/`) - Shows remote knowledge file entries through a virtual filesystem provider.

**Clone Workflow:**
1. User invokes Clone Agent → QuickPick for environment/agent (or paste URL)
2. LSP `powerplatformls/cloneAgent` request fetches agent
3. Writes `.mcs/conn.json` (connection metadata) to workspace
4. Adds workspace folder to VS Code, triggers post-open instruction

**Sync Operations (Agent Changes view + state machine):**
- **Preview (Fetch)** - `powerplatformls/getRemoteChanges` - read-only diff, no file writes
- **Get (Pull)** - `powerplatformls/syncPull` - downloads remote components to local
- **Apply (Push)** - fetches first, blocks if diagnostics have errors or non-knowledge remote changes exist, then uploads local changes via `powerplatformls/syncPush`
- States: `Idle → Fetching/Pulling/Pushing` - prevents concurrent operations and drives the `mcs.isSyncing` context key
- Classic SCM mode is deprecated in favor of the Agent Changes tree; related SCM code remains for backward compatibility but is not the active UX.

**File System Providers for Diffs:**
- `mcs-local://` - Original local file (from last sync)
- `mcs-remote://` - Current remote state
- `virtualKnowledge://` - Remote knowledge files
- VS Code's built-in diff view compares them

**Implemented LSP Custom Methods / Notifications:**
```
powerplatformls/onAgentDirectoryChange
powerplatformls/cloneAgent
powerplatformls/getCachedFile
powerplatformls/getLocalChanges
powerplatformls/getRemoteChanges
powerplatformls/getRemoteFile
powerplatformls/getWorkspaceDetails
powerplatformls/reattachAgent
powerplatformls/syncPull
powerplatformls/syncPush
workspace/listWorkspaces
```

### C# Language Server (`src/LanguageServers/`)

#### CLaSP Framework (`CLaSP/`)

Common Language Server Protocol Framework providing core abstractions:

- **`AbstractLanguageServer<TRequestContext>`** - Generic base implementing JSON-RPC communication. Manages request queue and handler provider lifecycle.
- **`IMethodHandler`** hierarchy - `IRequestHandler<TReq, TResp, TCtx>` (request/response), `INotificationHandler<TReq, TCtx>` (fire-and-forget). Routed by `LanguageServerEndpointAttribute` metadata.
- **`IRequestExecutionQueue<TRequestContext>`** - Serializes mutating requests, parallelizes read-only requests.
- **`HandlerProvider`** - Discovers handlers via reflection, routes by method name.
- **`ILspServices`** - Service container abstraction for DI.

#### PowerPlatformLS Solution (`PowerPlatformLS/`)

**Module Pattern:** Each language registers as an `ILspModule` with its own DI registrations:

| Module | Purpose |
|--------|---------|
| `CoreLspModule` | Base LSP infrastructure, shared handlers |
| `McsLspModule` | Copilot Studio language features |
| `PowerFxLspModule` | PowerFx expression support |
| `YamlLspModule` | YAML editing support |
| `PullAgentLspModule` | Agent sync and remote operations |

**Contract/Implementation Separation:**

| Project | Role |
|---------|------|
| `Contracts.FileLayout` | File path contracts (`AgentFilePath`), workspace interfaces (`IMcsWorkspace`), parser interfaces (`IMcsFileParser`) |
| `Contracts.Internal` | Core abstractions: `ILanguageAbstraction`, `RequestContext` (read-only struct), `LspDocument`, `Workspace` |
| `Contracts.LSP` | LSP model types, URI conversion utilities |
| `Impl.Core` | Language server host, request context resolution, core handlers (signature help, file watchers, code actions) |
| `Impl.Language.CopilotStudio` | MCS language: completion (BotElement schema), diagnostics, semantic tokens, go-to-definition |
| `Impl.Language.PowerFx` | PowerFx: expression completions, signature help, diagnostics |
| `Impl.Language.Yaml` | YAML: key/value completions, unique ID validation |
| `Impl.PullAgent` | Dataverse sync: clone, pull, push, remote file fetch, change detection |
| `Impl.YamlSourceTree` | YAML AST utilities for parsing and traversal |

#### Shared C# Sync/Core Projects (`src/`)

| Project | Role |
|---------|------|
| `CopilotStudio.McsCore` | Shared file-layout, projection, parsing, and path logic used by the LSP and sync layers |
| `CopilotStudio.Sync` | Reusable sync implementation for clone, pull, push, diffing, Dataverse access, and workspace projection |
| `CopilotStudio.Sync.UnitTests` / `CopilotStudio.Sync.E2ETests` / `CopilotStudio.Sync.TestHarness` | Tests and harnesses for the shared sync library |

**Authoring projection ownership:** ObjectModel owns agent primitives and their default file projection. `CopilotStudio.Sync` / `CopilotStudio.McsCore` owns VS Code and agent-specific projection behavior, including CliCopilot authoring-layout paths. Public Sync seams should expose that existing projection narrowly for consumers such as PAC; do not redesign projection or move PAC-owned Dataverse solution package emission into Sync.

**Language Routing:**
```
Request → LspUriFactory.FromJsonElement() → FileLspUri
       → WorkspacePath.TryGetLanguageType(filePath)
       → LanguageType (CopilotStudio | PowerFx | Yaml)
       → ILanguageAbstraction instance
       → Language-specific handler
```

**Pattern Detection:**
- `*.mcs.yml`, `*.mcs.yaml`, `botdefinition.json`, `icon.png` → Copilot Studio language
- `*.fx1`, PowerFx expressions in YAML properties → PowerFx language
- `*.yml`, `*.yaml` → YAML language

**Pluggable Rule Processors:**
- `CompletionRulesProcessor<DocType>` - Filters rules by trigger character, collects `CompletionItem[]`
- `ValidationRulesProcessor<DocType>` - Runs all rules, emits `Diagnostic[]` via `IDiagnosticsPublisher`
- Rules implement `ICompletionRule<T>` or `IValidationRule<T>` and are registered per document type

**Key Architectural Decisions:**
- `RequestContext` is a read-only struct (value semantics) preventing shared state mutations in async handlers
- Workspaces are resolved per agent directory with lazy document creation
- `LspUriFactory` abstracts URI scheme handling for future extensibility (git, ssh, vscode-remote)

#### LspJournalCli (`Tools/LspJournalCli/`)

Self-validating journal-based testing tool:
1. Load `.journal.json` (steps + expected responses)
2. Execute each step against a running LSP server
3. First run: RECORD baseline (creates `.pending/` shadow file)
4. Subsequent runs: PASS if actual matches expected
5. Material changes produce pending files for human review/acceptance

### Agent Workspace File Structure

```
agent-name/
  agent.mcs.yml         # Agent instructions
  settings.mcs.yml      # Agent entity properties
  connectionreferences.mcs.yml  # Connection references (when present)
  references.mcs.yml    # Cross-workspace/sub-agent references (when present)
  collection.mcs.yml    # Component collection metadata (when present)
  icon.png              # Agent icon (optional)
  actions/              # Task dialog components
  agents/               # Sub-agent folders and connected-agent actions/topics
  connectors/           # Custom connector definitions
  entities/             # Entity components
  environmentvariables/ # Environment variable definitions
  knowledge/            # One file per knowledge source
  knowledge/files/      # File-based knowledge sources
  prompts/              # AI Builder prompt definitions
  settings/             # Additional settings projections
  skills/               # Skill components
  topics/               # Topic components
  translations/         # Localized topic/component files
  trigger/              # Trigger components
  variables/            # Variable components
  workflows/            # Cloud flow workflow definitions
  .mcs/
    conn.json           # Connection metadata (dataverse endpoint, agent ID, account info)
    botdefinition.json  # Cached remote bot definition for diff/sync
    changetoken.txt     # Remote change token
    .knowledge-track.json  # Knowledge file change tracking
```

## Configuration

### Build Configuration

| File | Purpose |
|------|---------|
| `global.json` | .NET SDK 10.0.100, rollForward: latestFeature |
| `Directory.Build.props` | C# 12.0, nullable enable, implicit usings, docs generation |
| `Directory.Build.targets` | Central package management, assembly signing, Nullable.Extended.Analyzer |
| `src/Packages.props` | Centralized NuGet package versions |
| `version.json` | Nerdbank.GitVersioning: version 1.6, release branches `release/*` |
| `nuget.config` | NuGet package sources; currently lists nuget.org while some `Microsoft.Agents.*` dependencies still require internal restore access |
| `tsconfig.json` | TypeScript strict mode, ES2022 target, Node16 modules |
| `eslint.config.mjs` | Flat config: curly required, eqeqeq, no-throw-literal, semicolons |
| `esbuild.js` | Bundles `client/src/extension.ts` → `dist/extension.js` (CommonJS) |
| `extension.proj` | MSBuild targets for multi-platform LSP publish + VSIX packaging |

### Multi-Platform Publishing

The extension publishes LSP binaries for 6 platforms via `extension.proj`:
- `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`
- Each publish uses `--self-contained` and `PublishSingleFile`
- macOS uses x64 binary for both x64 and arm64 targets

### LSP Transport

- Windows: Named pipes
- macOS/Linux: Unix domain sockets
- Also supports stdio and JSON file-based communication
- CLI args: `--sessionid`, `--enabletelemetry`, `--file`/`--pipe`/`--stdio`

## Testing Strategy

### .NET Tests
- Framework: xUnit 2.4.1 with Moq 4.16.1
- Locations: `src/LanguageServers/PowerPlatformLS/UnitTests/PowerPlatformLS.UnitTests/`, `src/CopilotStudio.Sync.UnitTests/`, and `src/CopilotStudio.Sync.E2ETests/`
- 530+ unit tests + 13 LSP journal tests
- Test data in `UnitTests/PowerPlatformLS.UnitTests/TestData/`

### TypeScript Tests
- Framework: Node test runner inside a VS Code Extension Host via `@vscode/test-electron`
- Location: `client/src/tests/host/`
- Test workspace: `LanguageServers/PowerPlatformLS/UnitTests/PowerPlatformLS.UnitTests/TestData/WorkspaceWithSubAgents`
- Runner: `scripts/runHostTests.js` loads `client/out/tests/host/runner.js`

## CI/CD
- Pull requests to `main` run `.github/workflows/pr.yml`, which restores, builds, and tests `src/build.proj` in Debug configuration.
- Pushes to `main` run `.github/workflows/push.yml`, which triggers and monitors Azure DevOps pipeline `31673`.
- New issues are automatically labeled by `.github/workflows/auto-label-triage.yml`.

## Important Notes
- Do not commit registry/feed URLs to npm config or lockfiles; committed npm lockfiles should stay independent of restore registry.
- Expect external .NET restores/builds to fail until the internal `Microsoft.Agents.*` dependency is removed or restore access is configured.
