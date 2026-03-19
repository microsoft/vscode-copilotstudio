# LSP Journal CLI — Continuation

> **This file contains ONE next action.** When it is completed, log it in
> `journal-cli-completion-log.md`, then derive the next action from
> `journal-cli-roadmap.md` and replace this file's contents.

---

## Next action: Manual annotation pass (M3)

**Roadmap milestone:** M3: Review workflow qualifies behavior  
**Status:** Not started.

### Background

M2 is effectively complete (remaining items blocked on server team). The review
infrastructure from M3 is already built — step-level annotations, fingerprint
survival, `ReviewCommand`. What's missing is the actual annotation pass over the
~250 steps across 13 journals to mark known-good steps as `confirmed` and
known-suspect steps with `suspectId` references (S2–S6).

### Why this matters

Without annotations, every journal failure requires triage from scratch. Marking
steps as `confirmed` vs `suspect` means future failures immediately tell you
whether a verified behavior regressed or a known-suspect area shifted.

### What to do

1. Run `dotnet run -- review` to get the current annotation summary.
2. For each journal, review step baselines and annotate:
   - Steps with verified-correct server responses → `"review": "confirmed"`
   - Steps exhibiting known suspect behaviors (S2–S6) → `"review": "suspect"`,
     `"suspectId": "S<N>"`, with a brief `"reviewNote"`
   - Steps that are merely recorded (no opinion on correctness) → leave as-is
3. Focus on high-value journals first: `completion-depth`, `completion-error-context`,
   `all-the-things-completions`, `diagnostics`.
4. Run `dotnet run -- review --suspects-only` to verify suspect annotations are
   properly tagged.
5. Run `dotnet run -- --all` to confirm annotations don't affect execution.

### Done when

- All 13 journals have been reviewed and annotated where appropriate.
- `dotnet run -- review` shows a meaningful confirmed/suspect/unreviewed breakdown.
- `dotnet run -- --all` still passes 13/13.
- Roadmap M3 annotation pass item marked done.
- This entry logged in `journal-cli-completion-log.md`.
