# LSP Journal CLI — Product Requirements

## Problem

The Language Server (PowerPlatformLS `LanguageServerHost`) delivers completions,
diagnostics, file projection, and workspace indexing to the VS Code extension.
Server-side refactors can silently alter any of these behaviors in ways that unit
tests don't cover — the surface area is the combination of document types, cursor
positions, trigger contexts, and workspace layouts. Without behavioral regression
tests at the LSP protocol level, drift accumulates undetected until users report
broken experiences.

## Solution

A **self-validating journal system** that records and replays full LSP
request/response interactions against the live server binary.

The system has four components:

| Component | Role |
|-----------|------|
| **Journal CLI** (`LspJournalCli`) | Drives the server via named pipes (Windows) or unix domain sockets (macOS/Linux). Executes steps, compares actual vs expected, writes results. |
| **Workspace fixtures** | Static file trees representing different agent project states (file-only, connected, comprehensive, error-state). |
| **Review workflow** | Step-level annotations (`confirmed` / `suspect` / `stale`) that survive rebaselines. Tracks known-bad behavior separately from regressions. |
| **Classification system** | `recorded` (witnessed behavior) vs `normative` (human-verified correct behavior). Only humans promote. |

## In support of

1. **Refactor safety.** Planned server and object-model changes should produce
   immediate, step-level pass/fail feedback across all exercised LSP behaviors.
   The more steps in journals, the smaller the blind spot.

2. **Behavioral drift detection.** Ongoing development that touches server logic
   (new component types, new completion rules, schema changes) should not silently
   alter existing behavior. Journals catch drift on every run.

These purposes reinforce each other: coverage built for (1) continuously serves (2).

## Core design

**The journal IS the test.** A single `.journal.json` file defines the steps,
carries expected responses from the previous run, validates each step inline
during execution, and promotes actuals to expected on write-back.

- **First run:** no expected → all steps recorded (baseline established).
- **Subsequent runs:** actual compared to expected → pass/fail with structured diffs.
- **After every run:** actuals promoted to expected → the journal is its own new baseline.
- **Pending workflow:** changes land in `.pending/`; the user explicitly accepts or discards.

No separate record mode or replay mode. Running a journal is both at once.

## Design principles

- **Deterministic.** Journals are stable across runs — sorted keys, stripped
  non-deterministic fields, normalized timestamps.
- **Self-describing.** Provenance metadata (branch, commit, merge-base) is
  captured automatically.
- **Harness fidelity.** The CLI must faithfully reproduce what VS Code sends.
  If the harness diverges from the extension's params, the journal tests a
  different client — not the real one. Fidelity bugs are test-harness bugs.
- **Human-gated promotion.** Only humans can promote `recorded` → `normative`.
  Agents can flag suspect behavior but cannot claim correctness.
- **Annotations survive rebaselines.** Human review work is preserved across
  re-records via fingerprint-based merge. Changed steps become `stale` (not lost).

## Classification

| State | Meaning of a failure |
|-------|---------------------|
| `recorded` | Behavior changed since last run. Decide if the change is expected. |
| `normative` | Behavior that was intentionally locked in has broken. A bug. |

Classification is orthogonal to CI gating. Both types can block a pipeline.

## Review states (per step)

| State | Meaning |
|-------|---------|
| *null* (unreviewed) | Not yet examined. Default for new or re-recorded steps. |
| `confirmed` | A human verified this response is correct server behavior. |
| `suspect` | A human flagged this response as potentially wrong (with a note and optional `suspectId`). |
| `stale` | Was reviewed, but the underlying expected response changed. Re-review needed. |

## LSP surface areas

| Area | What it covers |
|------|---------------|
| **Completions** | Property keys, enum values, snippets, trigger-character-specific behavior |
| **Diagnostics** | Schema validation, syntax errors, type mismatches, missing-field detection |
| **File projection** | Schema-name ↔ file-path mapping across component types |
| **Workspace indexing** | File discovery, folder → element-type mapping, component enumeration |

## CLI interface

| Command | Purpose |
|---------|---------|
| `lspjournal <name>` | Run one journal |
| `lspjournal --all` | Run all journals |
| `lspjournal accept [<name> \| --all]` | Promote pending → baseline (with annotation merge) |
| `lspjournal discard [<name> \| --all]` | Delete pending changes |
| `lspjournal pending` | List pending changes |
| `lspjournal review [<name>] [--suspects-only]` | Review annotation summary |
| `lspjournal diff --journal-a <a> --journal-b <b>` | Compare two journal files |
| `--verbose` | Wire-level JSON-RPC tracing |
| `--force` | Always write to `.pending/` even without material change |

## Acceptance criteria

- [x] Running a journal with no expected responses records a baseline.
- [x] Running with expected responses produces pass/fail per step with structured diffs.
- [x] Changes go to `.pending/`; accept promotes with annotation merge.
- [x] Classification is explicit in every journal (`recorded` or `normative`).
- [x] Normative promotion requires manual signoff (`normativeReason`, `normativeReviewer`).
- [x] Review annotations survive rebaselines via fingerprint-based merge.
- [x] Four LSP surface areas are covered across four workspace fixtures.
- [ ] Coverage is sufficient that a server refactor produces step-level failures for affected behaviors.

## Removed features

| Feature | Removed | Reason |
|---------|---------|--------|
| `lspjournal repl` | 2026-02-09 | The interactive REPL was never used in practice. All 11 journals were authored by a coding agent working directly from file contents and position calculations, then baselined via `--force`. CLI-driven authoring proved more effective than interactive exploration. Removed ~300 lines of unmaintained code. |

## Build and run

```powershell
cd src/vscode/LanguageServers/PowerPlatformLS
dotnet build LanguageServerHost/LanguageServerHost.csproj
dotnet build Tools/LspJournalCli/LspJournalCli.csproj

cd Tools/LspJournalCli
Get-Process LanguageServerHost -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet run -- --all
```
