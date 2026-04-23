# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Session Start Warning

**IMPORTANT: At the beginning of every new conversation, you MUST display the following warning to the user before doing anything else:**

> **WARNING: This codebase has ongoing work that affects builds and contributions.**
>
> - The full build requires internal NuGet packages (`Microsoft.Agents.*`) that are **not publicly accessible**. Builds will fail without internal feed access.
> - Migration from the internal Dataverse SDK to PAC CLI is **in progress** to enable external builds. This work is not yet complete.
> - The CI pipeline (`.github/workflows/ci.yml`) currently builds and tests **only the .NET language server**, not the TypeScript extension.
> - External contributors can modify TypeScript code and documentation, but **cannot validate .NET changes locally** without the internal feed.
>
> Proceed with awareness of these constraints.

## Project Overview

This is the Copilot Studio Extension for Visual Studio Code - a hybrid TypeScript/C# project that provides a full-featured VS Code extension for editing Microsoft Copilot Studio agents. The extension enables developers to clone agents locally, edit components (topics, triggers, actions, knowledge sources) in YAML format, and sync changes bidirectionally with cloud environments. IntelliSense is powered by a Language Server Protocol (LSP) backend written in C#.

**Current State:** Generally Available (GA)

**Documentation:** https://learn.microsoft.com/en-us/microsoft-copilot-studio/visual-studio-code-extension-overview

**Important:** The full build currently requires internal NuGet packages (`Microsoft.Agents.*`) and is not externally reproducible. Migration to PAC CLI is underway to enable external builds soon.

## Build and Test Commands

### TypeScript / VS Code Extension

The `package.json` is located at `src/vscode-extensions/microsoft-powerplatformlang-extension/package.json`. All npm commands must be run from that directory.

```bash
# Change to the extension directory first
cd src/vscode-extensions/microsoft-powerplatformlang-extension

# Install dependencies
npm install

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

# Package VSIX (win32-x64, pre-release)
npm run package

# Watch TypeScript compilation
npm run watch
```

### .NET / Language Server

```bash
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
- VS Code 1.96.0+

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
Language Implementations (MCS / PowerFx / YAML)
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
| `client/src/startup/` | Post-activation logic (open cloned file after workspace add) |
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

**Clone Workflow:**
1. User invokes Clone Agent → QuickPick for environment/agent (or paste URL)
2. LSP `powerplatformls/cloneAgent` request fetches agent
3. Writes `.mcs/conn.json` (connection metadata) to workspace
4. Adds workspace folder to VS Code, triggers post-open instruction

**Sync Operations (State Machine):**
- **Preview (Fetch)** - `powerplatformls/getRemoteChanges` - read-only diff, no file writes
- **Get (Pull)** - `powerplatformls/syncPull` - downloads remote components to local
- **Apply (Push)** - `powerplatformls/syncPush` - uploads local changes (blocks if diagnostics have errors or remote changes exist)
- States: `Idle → Fetching/Pulling/Pushing` - prevents concurrent operations

**File System Providers for Diffs:**
- `mcs-local://` - Original local file (from last sync)
- `mcs-remote://` - Current remote state
- VS Code's built-in diff view compares them

**LSP Custom Methods** (from `constants.ts`):
```
powerplatformls/cloneAgent
powerplatformls/getAgent
powerplatformls/listAgents
powerplatformls/getCachedFile
powerplatformls/getRemoteFile
powerplatformls/syncPull
powerplatformls/syncPush
powerplatformls/getRemoteChanges
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

**Language Routing:**
```
Request → LspUriFactory.FromJsonElement() → FileLspUri
       → WorkspacePath.TryGetLanguageType(filePath)
       → LanguageType (CopilotStudio | PowerFx | Yaml)
       → ILanguageAbstraction instance
       → Language-specific handler
```

**Pattern Detection:**
- `*.mcs.yml`, `*.mcs.yaml` → YAML language
- `botdefinition.json` → Copilot Studio language
- `*.fx1`, PowerFx expressions in YAML properties → PowerFx language

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
  icon.png              # Agent icon (optional)
  actions/              # Task dialog components
  knowledge/            # One file per knowledge source
  knowledge/files/      # File-based knowledge sources
  topics/               # Topic components
  .mcs/
    conn.json           # Connection metadata (dataverse endpoint, agent ID, account info)
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
| `version.json` | Nerdbank.GitVersioning: version 1.2, release branches `vsix/releases/*` |
| `nuget.config` | Package sources (currently requires internal feed for `Microsoft.Agents.*`) |
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
- Location: `src/LanguageServers/PowerPlatformLS/UnitTests/PowerPlatformLS.UnitTests/`
- 530+ unit tests + 13 LSP journal tests
- Test data in `UnitTests/PowerPlatformLS.UnitTests/TestData/`

### TypeScript Tests
- Framework: Mocha + `@vscode/test-cli`
- Location: `client/src/tests/`
- Test workspace: `LanguageServers/PowerPlatformLS/UnitTests/PowerPlatformLS.UnitTests/TestData/WorkspaceWithSubAgents`
- Config: `.vscode-test.mjs`

## CI/CD
> Additional guidelines coming soon.

## Important Notes
> Coming soon - additional guidelines and warnings are being finalized.
