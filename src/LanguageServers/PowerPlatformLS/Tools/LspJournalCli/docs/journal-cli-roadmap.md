# LSP Journal CLI — Roadmap

Milestones are ordered by payoff event — the moment where the investment starts
returning value. Each milestone builds on the previous one.

---

## Acceptance blockers — URGENT

**Gate for mainline acceptance:** The CLI will not be accepted into mainline
until all items below are complete.

| Work item | Priority | Why it blocks |
|-----------|----------|---------------|
| ~~Handle server-initiated JSON-RPC requests (e.g., `workspace/configuration`, `client/registerCapability`)~~ | ~~URGENT~~ | **DONE.** Transport detects server requests (method + id), responds with method-aware defaults (`[]` for workspace/configuration, `null` for others), supports custom handler overrides, and surfaces handler errors as JSON-RPC errors. 7 unit tests + integration journal. |
| ~~Deterministic notification ordering~~ | ~~URGENT~~ | **DONE.** Notification comparison and material-change detection now use order-insensitive multiset matching, eliminating reorder-only churn. |
| ~~Material change detection accounts for extra notifications~~ | ~~URGENT~~ | **CLOSED — by design.** Extra notifications are non-deterministic noise (diagnostic waves, progress, intermediate clears); treating them as material changes would create flaky pending diffs. The harness contract is "did the server produce the behavior I asserted?" not "did it produce only that behavior." Missing expected notifications already trigger failure via multiset match. If a new notification species carries regression signal, that's better caught by a dedicated journal step asserting on it, not by blanket extra-detection. |
| ~~Diagnose server shutdown flakiness (product-side root cause)~~ | ~~URGENT~~ | **DONE.** Three product-side bugs fixed: (a) `JsonRpcStream` silently swallowed null results for requests — shutdown never sent a response; (b) default `ConsoleLoggerProvider` wrote to stdout, corrupting JSON-RPC framing; (c) `Program.cs` startup `Console.WriteLine` calls polluted stdout. Also fixed journal CLI `JsonElement?` null round-trip (`NullableJsonElementConverter` with `HandleNull => true`). 13/13 journals pass, baselines stable, no orphan processes. |

## M1: Core tool catches behavioral drift — DONE

**Payoff event:** Someone changes server code, runs `lspjournal --all`, and gets
immediate pass/fail feedback across 9 journals and ~185 steps.

**What was built:**
- Self-validating journal model (steps carry expected/actual/status/diff inline)
- CLI with run, accept, discard, pending, diff commands
- Server auto-discovery, path portability (`${workspace}` tokens), git metadata
- Deterministic output normalization
- 4 workspace fixtures (file-only, connected, all-the-things, all-the-things-clean)
- 9 passing journals covering completions, diagnostics, indexing, file projection
- Pending workflow with material change detection
- *(REPL was built here but removed post-M1 — coding-agent CLI use proved more
  effective than interactive exploration; ~300 lines removed.)*

**Status:** Complete. All infrastructure is operational.

---

## M2: Coverage makes refactors safe — IN PROGRESS

**Payoff event:** A refactor to the object model, node system, or completion rules
is covered by enough journals that silent behavioral drift is unlikely to escape
undetected. This is the milestone where the tool's value *compounds* — each new
journal step permanently reduces the blind spot.

**What remains:**

| Work item | Priority | Blocked by | Why it matters |
|-----------|----------|------------|----------------|
| ~~Completion coverage depth~~ | ~~HIGH~~ | — | **DONE.** `completion-depth` and `completion-error-context` journals cover property-key, enum-value, 4 trigger characters, and error-state completion. |
| ~~Investigate LoopAndBranch missing diagnostics~~ | ~~HIGH~~ | — | **DONE.** Harness issue: fixture file had YAML-escaped quotes differing from original inline text; corrected fixture bytes and journal hashes. |
| ~~Resolve LoopAndBranch completion delta~~ | ~~MEDIUM~~ | — | **DONE.** Ran `all-the-things-completions` (64/64 pass, unchanged). No pending deltas for LoopAndBranch — corrected fixture produces identical completions. |
| ~~Accept completion `textEdit` fields in baselines~~ | ~~MEDIUM~~ | — | **DIAGNOSED — not a product improvement.** CRLF line endings from fixture files cause phantom `\r` at cursor position; server generates spurious `textEdit` replacing it. Fix is to normalize line endings in `DocumentTextPolicy.ExpandTextNode`. See completion log 2026-02-10. |
| ~~Normalize line endings in `DocumentTextPolicy`~~ | ~~MEDIUM~~ | — | **DONE.** `ExpandTextNode` and `ScrubTextDocument` now normalize `\r\n` → `\n` before hashing and text assignment, matching VS Code's `didOpen` behavior. `completion-depth` pending discarded; all 13/13 journals pass clean with LF-normalized hashes. |
| File projection breadth | HIGH | Agent doc crash | Complex mapping paths (GPT special case, sub-agent folders, dotted filenames, qualified schema names) are the most fragile code and the most likely to break during refactors. |
| Fix Agent doc server crash | HIGH | Server team | Server crashes on `kind: Agent` documents, which prevents file-projection journal from expanding. This is a server bug, not a journal-CLI issue. |
| ~~Workspace indexing edge cases~~ | ~~MEDIUM~~ | — | **DONE.** `workspace-indexing-edges` journal covers empty directories, unexpected file extensions, folder→element-type assertions (topics, actions, entities), and type→file-candidate resolution. Also discovered: opening `.txt` or unknown-folder `.mcs.yml` files crashes the server session (language resolution failure). |

**Current state:** 13 journals, ~250 steps. Fixture inventory covers 26+ component
types across 5 fixtures. Priority 1 fixture expansion done (RegexEntity, SharePointSearchSource,
ConditionGroup, Foreach, OAuthInput, InvokeFlowTaskAction, AutonomousSettings,
OnRedirect, OnError, OnActivity). Workspace indexing edge cases done. Completion
depth done. Line-ending normalization done — baselines are now OS-independent.
See [journal-cli-test-matrix.md](journal-cli-test-matrix.md)
for cell-level coverage tracking across component types × LSP lenses.

---

## M3: Review workflow qualifies behavior — PARTIALLY DONE

**Payoff event:** When a journal fails, you can distinguish between *expected
breakage* (a step already marked `suspect`) and *real regression* (a `confirmed`
step that changed). This transforms journal failures from "something changed" into
"something that was working broke."

**What's done:**
- Step-level review annotations (`review`, `reviewNote`, `suspectId` on JournalStep)
- Fingerprint-based annotation survival across rebaselines (`StepFingerprint`)
- `ReviewCommand` with summary, detail, and `--suspects-only` views
- S1 resolved (CompletionContext fix)
- S2 investigated (connector ref validation doesn't fire in detached mode)

**What remains:**

| Work item | Priority | Why it matters |
|-----------|----------|----------------|
| Manual annotation pass (~185 steps) | MEDIUM | Without annotations, every failure requires triage from scratch. Annotating known-good steps as `confirmed` and known-suspect steps (S3–S5) makes future failures immediately actionable. |
| Auto-detection heuristics | LOW | Flag patterns like empty completions at valid positions, error-named files with no diagnostics. Stretch goal — reduces manual review burden. |

---

## M4: Normative confidence — NOT STARTED

**Payoff event:** CI can gate on normative journals — a normative failure is a
*blocker*, a recorded failure is a *review item*. Separates "behavior changed" from
"a bug was introduced."

**Depends on:** M2 (sufficient coverage) and M3 (annotation pass). Premature
promotion wastes effort — behavior must be verified correct before locking it in.

| Work item | Priority | Why it matters |
|-----------|----------|----------------|
| Select stable journals for normative promotion | LOW | Journals where expected responses have been human-verified as correct server behavior. |
| CI integration | LOW | Not yet needed. When the suite is stable, determine which journals gate the pipeline. |

---

## M5: Attached mode — NOT STARTED

**Payoff event:** Journals exercise the attached-workspace code path (agent
connected to a Dataverse environment), catching regressions in a distinct server
mode that file-only journals can't reach.

**Depends on:** M2 (core coverage established first). Exploratory — no real
Dataverse backend is available for testing.

| Work item | Priority | Why it matters |
|-----------|----------|----------------|
| Attached-workspace fixture (`references.mcs.yml`) | MEDIUM | Simulates a connected workspace. Exercises reattach flow, connection reference resolution. |
| Document behavioral differences | MEDIUM | The server behaves differently in attached vs detached mode. Understanding the delta informs what additional journals are needed. |

---

## Suspect tracker

Active suspects carried from investigation work. These are annotated in journals
and tracked here for visibility.

| ID | Behavior | Journal | Status |
|----|----------|---------|--------|
| S1 | Empty completion responses | completions | **RESOLVED** — CompletionContext was missing. Fixed. |
| S2 | BrokenConnector.mcs.yml: zero diagnostics for dangling connector ref | diagnostics | **INVESTIGATED** — validation doesn't fire in detached mode. Remains suspect; candidate for filing. |
| S3 | MissingFields.mcs.yml: zero diagnostics for ExternalTriggerConfiguration with only `kind:` | diagnostics | **OPEN** — no validation fires for missing required fields. |
| S4 | SyntaxError.mcs.yml: zero diagnostics despite name implying errors | diagnostics | **OPEN** — may be a fixture content issue rather than a server bug. |
| S5 | InvalidEntity.mcs.yml: duplicate diagnostic ranges for different missing properties | diagnostics | **OPEN** — two MissingRequiredProperty errors share exact same range. Possibly correct. |
| S6 | Opening .txt or unknown-folder .mcs.yml files crashes server session (language resolver throws, shutdown hangs) | workspace-indexing-edges | **OPEN** — discovered during edge-case journal authoring. Server fails to get language for non-standard files and corrupts session state. |
