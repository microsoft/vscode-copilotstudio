```markdown
# All-the-things CLEAN workspace fixture (disconnected — no .mcs directory)
#
# Identical to all-the-things-workspace but WITHOUT intentionally broken files.
# Error files (InvalidEntity, InvalidScope, BrokenConnector, MissingFields) live
# in the original all-the-things-workspace for the diagnostics journal.
#
# This clean variant is used for indexing + completions journals so that
# diagnostic noise from error files does not leak into those tests.
#
# Document types included:
#   - agent.mcs.yml                  (GptComponentMetadata — root agent)
#   - settings.mcs.yml               (BotEntity — workspace settings)
#   - connectionreferences.mcs.yml   (ConnectionReferencesSourceFile)
#   - topics/                         (AdaptiveDialog — conversation topics)
#   - actions/                        (TaskDialog — connector/agent/flow invocations)
#   - agents/                         (AgentDialog — sub-agent definitions)
#   - variables/                      (Variable — global variables)
#   - translations/                   (AdaptiveDialog — localized topics)
#   - knowledge/                      (PublicSiteSearchSource — knowledge sources)
#   - knowledge/files/                (file attachment metadata stubs)
#   - entities/                       (ClosedListEntity — custom entities)
#   - skills/                         (SkillDefinition — skill definitions)
#   - trigger/                        (ExternalTriggerConfiguration — external triggers)
#   - settings/                       (BotSettingsComponent — moderation, feedback)

```