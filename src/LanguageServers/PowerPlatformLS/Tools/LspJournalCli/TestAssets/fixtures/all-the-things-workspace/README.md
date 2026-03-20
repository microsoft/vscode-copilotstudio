# All-the-things workspace fixture (disconnected — no .mcs directory)
#
# Contains every document type the Language Server is expected to handle.
# Used for comprehensive exploratory testing of completions, diagnostics,
# file projection, and workspace indexing across the full surface area.
#
# Document types included:
#   - agent.mcs.yml          (GptComponentMetadata — root agent)
#   - settings.mcs.yml       (BotEntity — workspace settings)
#   - topics/                 (AdaptiveDialog — conversation topics)
#   - actions/                (TaskDialog — connector/agent invocations)
#   - agents/                 (AgentDialog — sub-agent definitions)
#   - variables/              (Variable — global variables)
#   - translations/           (AdaptiveDialog — localized topics)
#   - knowledge/              (PublicSiteSearchSource — knowledge sources)
#   - knowledge/files/        (file attachment metadata stubs)
#   - entities/               (ClosedListEntity — custom entities)
#   - skills/                 (SkillDefinition — skill definitions)
#   - trigger/                (ExternalTriggerConfiguration — external triggers)
#   - settings/               (BotSettingsComponent — moderation, feedback)
