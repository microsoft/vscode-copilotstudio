---
applyTo: '**/*.{ts,tsx,cs}'
---

# Code Review Patterns

Cross-cutting patterns that recur in PR review feedback for the Copilot Studio VS Code Extension. These are not auto-detected by ESLint or Roslyn analyzers — check them manually before pushing.

> **High-frequency review flags:**
>
> - Missing `await` on async LSP requests (silent failures)
> - PII leaked in telemetry without `<pii>` tag wrapping
> - Concurrent sync operations not guarded by state machine
> - Command handlers not pushed to `context.subscriptions`
> - Disposable resources not cleaned up on deactivation
> - Hardcoded environment URLs instead of cluster lookup
> - Missing error handling on Dataverse/BAP API calls
> - `any` type usage bypassing TypeScript strict mode
> - C# handlers missing null checks on nullable-enabled types

## TypeScript Extension Patterns

### Command Registration Must Use Subscriptions

Every `commands.registerCommand` call must push the disposable to `context.subscriptions`. Leaked commands survive extension deactivation and cause ghost behavior on reload.

```typescript
// BAD - disposable leaked
commands.registerCommand('microsoft-copilot-studio.x', handler);

// GOOD - tracked for cleanup
const command = commands.registerCommand('microsoft-copilot-studio.x', handler);
context.subscriptions.push(command);
```

### Async LSP Requests Must Be Awaited

The LSP client proxy wraps all requests with telemetry. Forgetting `await` silently drops errors and makes the telemetry log "success" for failed operations.

```typescript
// BAD - fire-and-forget on LSP request
lspClient.sendRequest('powerplatformls/syncPush', params);

// GOOD - await and handle errors
const result = await lspClient.sendRequest('powerplatformls/syncPush', params);
```

### PII Must Use Redaction Tags

The logger redacts `<pii>text</pii>` in telemetry output. Any user-identifiable data (environment IDs, agent names, email addresses, URLs with tenant info) must be wrapped.

```typescript
// BAD - environment URL exposed in telemetry
logger.info(`Connecting to ${environmentUrl}`);

// GOOD - PII redacted in telemetry stream
logger.info(`Connecting to <pii>${environmentUrl}</pii>`);
```

### Sync State Machine Must Block Concurrent Operations

The sync system uses states (`Idle`, `Fetching`, `Pulling`, `Pushing`). Any new operation must check current state before proceeding. A push during a fetch creates data races.

```typescript
// BAD - no state guard
async function syncPush() {
  await lspClient.sendRequest('powerplatformls/syncPush', params);
}

// GOOD - state guard prevents concurrent operations
async function syncPush() {
  if (syncState !== SyncState.Idle) {
    logger.warn('Sync operation already in progress');
    return;
  }
  syncState = SyncState.Pushing;
  try {
    await lspClient.sendRequest('powerplatformls/syncPush', params);
  } finally {
    syncState = SyncState.Idle;
  }
}
```

### No Hardcoded Environment URLs

Use the cluster lookup utility for environment-specific URLs. Hardcoded URLs break in sovereign clouds (GCC High, Mooncake, DoD).

```typescript
// BAD - hardcoded to commercial cloud
const endpoint = `https://api.powerplatform.com/environments/${envId}`;

// GOOD - cluster-aware lookup
const endpoint = getClusterEndpoint(cluster, envId);
```

### Error Handling on External API Calls

Dataverse and BAP clients can return 401 (token expired), 403 (permanent deny), 404 (deleted), or 5xx. Each needs distinct handling — don't catch-all with a generic message.

```typescript
// BAD - swallows all errors
try {
  await dataverseClient.getAgents(envId);
} catch {
  showError('Something went wrong');
}

// GOOD - discriminated error handling
try {
  await dataverseClient.getAgents(envId);
} catch (e) {
  if (e.statusCode === 403) {
    dataverseClient.markPermanentFailure(envId);
    showError('Access denied to this environment');
  } else if (e.statusCode === 401) {
    await refreshToken();
  } else {
    showError(`Failed to list agents: ${e.message}`);
  }
}
```

### Tree View Refresh Must Be Debounced

The Remote Agents Tree and Agent Changes Tree use debounced refresh. Adding a non-debounced `refresh()` call in a loop causes UI thrashing.

```typescript
// BAD - refresh per item in a loop
for (const agent of agents) {
  treeProvider.refresh();
}

// GOOD - single debounced refresh after batch
agents.forEach(processAgent);
treeProvider.refresh(); // debounced internally
```

## C# Language Server Patterns

### RequestContext Is a Read-Only Struct

`RequestContext` uses value semantics to prevent shared state mutation in async handlers. Never store it in a field or pass by reference to long-lived objects.

```csharp
// BAD - storing struct in a field allows stale reads
private RequestContext _context;
public void Handle(RequestContext ctx) { _context = ctx; }

// GOOD - use within handler scope only
public Task HandleAsync(RequestContext ctx, CancellationToken ct)
{
    var workspace = ctx.Workspace;
    // ... use within this scope
}
```

### Nullable Reference Types Must Be Checked

With `<Nullable>enable</Nullable>` in `Directory.Build.props`, all reference types are non-null by default. Nullable parameters (`string?`, `Document?`) must be checked before use.

```csharp
// BAD - potential NullReferenceException
public CompletionItem[] GetCompletions(LspDocument? document)
{
    return document.GetTokens(); // nullable dereference
}

// GOOD - early return on null
public CompletionItem[] GetCompletions(LspDocument? document)
{
    if (document is null) return Array.Empty<CompletionItem>();
    return document.GetTokens();
}
```

### Handler Registration via Attributes

LSP handlers are discovered via `[LanguageServerEndpoint("method/name")]`. Forgetting the attribute means the handler is never routed to — it compiles but silently does nothing.

```csharp
// BAD - missing attribute, handler never called
public class MyHandler : IRequestHandler<MyRequest, MyResponse, RequestContext>
{
    public Task<MyResponse> HandleRequestAsync(...) { ... }
}

// GOOD - properly attributed
[LanguageServerEndpoint("powerplatformls/myMethod")]
public class MyHandler : IRequestHandler<MyRequest, MyResponse, RequestContext>
{
    public Task<MyResponse> HandleRequestAsync(...) { ... }
}
```

### DI Registration Must Match Module

Each `ILspModule` registers its own services. Adding a handler to `McsLspModule` but depending on a service registered only in `PullAgentLspModule` causes runtime DI failures, not compile-time errors.

```csharp
// BAD - using IRemoteAgentService in McsLspModule handler
// (only registered by PullAgentLspModule)
[LanguageServerEndpoint("powerplatformls/agentCompletion")]
public class AgentCompletionHandler
{
    private readonly IRemoteAgentService _remote; // ❌ not in this module's DI
}

// GOOD - keep handlers in the module that registers their dependencies
```

### Workspace Resolution Is Per-Agent-Directory

Workspaces are resolved by agent directory (containing `.mcs/conn.json`). Don't assume a single global workspace — multi-root VS Code workspaces have multiple agents.

## General Patterns (Both Languages)

### No Secrets or Tokens in Logs

Even in debug-level logging, never log tokens, connection strings, or auth headers. The log output may be captured in diagnostics or telemetry.

### File Path Handling Must Be Cross-Platform

The extension runs on Windows (named pipes), macOS, and Linux (Unix sockets). Use `path.join()` / `Path.Combine()` — never string concatenation with `/` or `\`.

### Breaking LSP Contract Changes Need Both Sides

If you change an LSP request/response shape in C#, the TypeScript client must be updated in the same PR. Mismatched contracts cause silent deserialization failures at runtime.
