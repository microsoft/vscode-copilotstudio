# Journal files are stored here.
#
# Each *.journal.json is a self-validating test:
# - classification: "recorded" — witnessed behavior, replays to detect drift.
# - classification: "normative" — verified correct behavior, gates validation.
#
# Workflow:
# 1. Explore in the REPL, save interesting sessions as journals.
# 2. Replay captures actual server responses as `expected` values.
# 3. To promote: set classification to "normative", add normativeReason/Reviewer.
