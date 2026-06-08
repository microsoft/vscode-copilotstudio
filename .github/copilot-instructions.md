# Copilot Studio VS Code Extension - Copilot Instructions

**Copilot Studio Extension** is a hybrid TypeScript/C# VS Code extension for editing Microsoft Copilot Studio agents locally. It uses LSP for IntelliSense powered by a .NET backend.

## Architecture

```
VS Code Extension (TypeScript) → JSON-RPC → LanguageServerHost.exe (C# / .NET 10)
```

- **TypeScript extension:** `src/vscode-extensions/microsoft-powerplatformlang-extension/`
- **C# Language Server:** `src/LanguageServers/PowerPlatformLS/`

### Data Flow

```
Commands/TreeViews → LSP Client (singleton) → JSON-RPC → C# Handlers → Dataverse/BAP APIs
```

## Essential Commands

```bash
cd src/vscode-extensions/microsoft-powerplatformlang-extension
npm install                    # Install dependencies
npm run compile                # Full build (LSP + TS)
npm run check-types            # Type check only
npm run lint                   # ESLint
npm test                       # VS Code extension tests

dotnet build src/LanguageServers/PowerPlatformLS/PowerPlatformLS.sln    # Build LSP
dotnet test src/LanguageServers/PowerPlatformLS/PowerPlatformLS.sln     # .NET tests
```

## Critical Rules

### DO NOT

- Log tokens, auth headers, or connection strings (even at debug level)
- Hardcode environment URLs (use cluster lookup for sovereign clouds)
- Use `any` type — TypeScript strict mode is enforced
- Make breaking LSP contract changes without updating both TS and C# sides
- Fire-and-forget async LSP requests (always `await`)
- Assume single-workspace — multi-root workspaces have multiple agents

### ALWAYS

- Push command disposables to `context.subscriptions`
- Wrap PII in `<pii>` tags for logger/telemetry redaction
- Guard sync operations with the state machine (prevent concurrent push/pull/fetch)
- Use `path.join()` / `Path.Combine()` for cross-platform file paths
- Check nullable references in C# handlers (`<Nullable>enable</Nullable>` is global)
- Add `[LanguageServerEndpoint]` attribute to new LSP handlers

## Pull Request Workflow

1. Branch: `<alias>/<description>`
2. Verify: `npm run lint && npm run check-types && npm test` (TypeScript) + `dotnet test` (.NET)
3. Commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`
4. Keep PRs focused on a single concern — target < 800 changed lines

## Code Review

PRs are reviewed by a **dual-model adversarial review pair** to eliminate single-model blind spots:

- **`reviewer-opus`** (Claude Opus 4.8) — high-reasoning reviewer
- **`reviewer-gpt`** (GPT-5.5) — independent second opinion on a different model

Both run in parallel on every diff. A finding raised by only one model still counts, but should be verified against the actual code before accepting as blocking. The reviewers follow the 1ES anti-false-positive rules (6 mandatory checks before reporting).

Review lenses: correctness, security, performance (with proportional impact), resilience, architecture, and convention violations from `.github/instructions/CodeReviewPatterns.instructions.md`.

## Key Reference Files

| Pattern | File |
|---------|------|
| Command registration | `client/src/extension.ts` |
| LSP client singleton | `client/src/services/lspClient.ts` |
| Logger with PII redaction | `client/src/services/logger.ts` |
| Auth + cluster scopes | `client/src/clients/account.ts` |
| Sync state machine | `client/src/sync/` |
| LSP handler example | `src/LanguageServers/PowerPlatformLS/Impl.Core/` |
| DI module registration | Any `*LspModule.cs` file |
