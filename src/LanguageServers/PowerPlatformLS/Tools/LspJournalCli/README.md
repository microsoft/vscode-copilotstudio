# LSP Journal CLI

Run journals against the Language Server, validate inline, produce self-validating output.

A journal is one file that IS the test: it defines steps, carries expected responses from the
previous run, and validates each step inline during execution. Recording and replay are the
same operation.

## Quick Start

```powershell
# Build (once)
dotnet build src/vscode/LanguageServers/PowerPlatformLS/Tools/LspJournalCli/LspJournalCli.csproj
dotnet build src/vscode/LanguageServers/PowerPlatformLS/LanguageServerHost/LanguageServerHost.csproj

# Run one journal
dotnet run --project src/vscode/LanguageServers/PowerPlatformLS/Tools/LspJournalCli -- lifecycle

# Run all journals
dotnet run --project src/vscode/LanguageServers/PowerPlatformLS/Tools/LspJournalCli -- --all

# Wire-level tracing (any command)
dotnet run --project src/vscode/LanguageServers/PowerPlatformLS/Tools/LspJournalCli -- lifecycle --verbose
```

The server binary is found automatically from the build output. No flags needed.

## How It Works

1. You run `lifecycle` (or any journal name)
2. The CLI loads `TestAssets/journals/lifecycle.journal.json`
3. Each step executes against the LSP server via named pipes (Windows) or unix domain sockets (macOS/Linux)
4. If the step has an expected response (from a previous run), it validates inline — pass/fail
5. If no expected response exists, it records the actual response as baseline
6. **If all steps pass with no behavioral change, the journal file is NOT modified** (zero git diff)
7. If a material change is detected (new recording, or expected ≠ actual), a **pending file** is written to `.pending/`
8. Console shows pass/fail per step. Exit code 0 = all passed.

**First run**: all steps show "RECORDED" — establishing the baseline. Pending file created.
**Subsequent runs (no change)**: steps show "pass", file is untouched, `git status` is clean.
**Behavioral change**: failing steps show "FAIL", pending file created for review.

The journal is self-validating: **you never need to diff two files**. The pass/fail
status is determined during execution and reported on the console.

## Commands

| Usage | What it does |
|-------|--------------|
| `<name>` | Run one journal by name |
| `--all` | Run all journals |
| `accept <name>` | Accept pending changes for one journal |
| `accept --all` | Accept all pending changes |
| `discard <name>` | Discard pending changes for one journal |
| `discard --all` | Discard all pending changes |
| `pending` | List pending changes |
| `diff --journal-a <a> --journal-b <b>` | Compare two journal files (utility) |

## Stable-on-Pass (Pending Workflow)

The runner **never overwrites** a journal baseline in place. When behavior changes:

1. The runner writes a **shadow file** to `.pending/<name>.journal.json`
2. The original baseline remains untouched
3. You review the pending file and decide whether to accept or discard

```powershell
# Run all journals
dotnet run -- --all

# See what's pending
dotnet run -- pending

# Accept changes for a specific journal
dotnet run -- accept lifecycle

# Accept all pending changes
dotnet run -- accept --all

# Discard all pending changes
dotnet run -- discard --all
```

**Key invariant:** A passing run with no behavioral change produces **zero git diff**.
A `git commit` touching a journal file means the server's behavior materially changed.

### What counts as a material change?

| Category | Triggers pending file? |
|----------|----------------------|
| Step expected ≠ actual response | Yes |
| Step expectedNotifications ≠ actual | Yes |
| Step has no expected (first recording) | Yes |
| Step expects no notifications but receives any | Yes |
| Extra unexpected notifications beyond expected list | No (warning only) |
| Metadata timestamp/commit/branch differ | No (not material) |

### Metadata enrichment

When a material change IS detected, the pending file includes richer git provenance:

- `branchBase` — merge-base commit with `main`
- `branchDepth` — number of commits since diverging from `main`
## Journals

Journals live in `TestAssets/journals/<name>.journal.json`:

| Journal | Fixture | What it exercises |
|---------|---------|-------------------|
| `lifecycle` | file-only | initialize → shutdown → exit |
| `smoke-test` | file-only | Open doc, completions, shutdown |
| `diagnostics` | file-only | Syntax errors, schema validation, type mismatches |
| `workspace-indexing` | connected | File discovery, completions |
| `file-projection` | connected | Schema names across topics/actions/agents |

## Journal Format

```json
{
  "metadata": {
    "description": "What this test covers",
    "workspaceRoot": "../fixtures/file-only-workspace",
    "classification": "recorded"
  },
  "steps": [
    {
      "step": "initialize",
      "params": { "processId": 0, "capabilities": {}, "workspaceFolders": [...] },
      "expected": { "...": "response from previous run" }
    },
    {
      "step": "open",
      "params": { "textDocument": { "uri": "...", "languageId": "CopilotStudio", "version": 1, "text": "..." } },
      "waitFor": [{ "method": "textDocument/publishDiagnostics", "timeoutMs": 10000 }],
      "expected": null,
      "expectedNotifications": [{ "method": "textDocument/publishDiagnostics", "params": { "...": "..." } }]
    },
    { "step": "shutdown" },
    { "step": "exit" }
  ]
}
```

- `expected` — response from the last run. Absent on first run. Validated inline on subsequent runs.
- `waitFor` — notifications to wait for after executing the step.
- `expectedNotifications` omitted or empty means the step is intended to be quiet; any notifications are treated as a material change.
- `textDocument.text` can be stored as `${file:relative/path}` with `textHash` and `textBytes` metadata. The runner rehydrates the text from disk at execution time and fails if the hash no longer matches.
- `classification` — describes what a failure means:
  - `"recorded"` — behavior changed from what was observed. You decide if the change is expected.
  - `"normative"` — behavior that was reviewed and intentionally locked in has broken.
  - Classification is orthogonal to CI gating. Which journals run in CI is determined by selection, not classification.

### Normative promotion

Classification starts as `"recorded"`. When a human reviews the expected responses and
confirms they represent correct behavior, they edit the journal metadata to promote it:

```json
{
  "classification": "normative",
  "normativeReason": "Reviewed: completions match expected schema.",
  "normativeReviewer": "alias"
}
```

Agents can detect drift, flag changes, and question whether observed behavior is correct — but only a human can promote to normative.

## Step Types

| Step | Type | LSP Method |
|------|------|------------|
| `initialize` | request | `initialize` |
| `initialized` | notification | `initialized` |
| `open` | notification | `textDocument/didOpen` |
| `close` | notification | `textDocument/didClose` |
| `change` | notification | `textDocument/didChange` |
| `completion` | request | `textDocument/completion` |
| `diagnostics` | wait | `textDocument/publishDiagnostics` |
| `shutdown` | request | `shutdown` |
| `exit` | notification | `exit` |

Any unrecognized step name is sent as a generic JSON-RPC request.

## Troubleshooting

- **`--verbose`** dumps every JSON-RPC message to stderr.
- **"Timed out"** — increase `timeoutMs` in the step's `waitFor`, or the server crashed earlier in the sequence.
- **"Server was requested to shut down"** — a prior step crashed the server's request queue. Check stderr.

