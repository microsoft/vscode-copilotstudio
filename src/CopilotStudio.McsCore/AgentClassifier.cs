namespace Microsoft.CopilotStudio.McsCore
{
    using System;
    using System.IO;
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.ObjectModel.Yaml;

    /// <summary>
    /// Bridge-era classifier that resolves an agent's <see cref="AuthoringShape"/> from
    /// cloud evidence and a workspace's <see cref="WorkspaceLayout"/> from disk, and
    /// combines them into a single <see cref="AgentClassification"/> (PRD R1, R7).
    /// Classification lives in one place so surfaces do not each re-implement detection.
    /// </summary>
    /// <remarks>
    /// Interim inference, pending a first-class authoring-shape signal on the entity: the shape
    /// is inferred here from the entity's own configuration. A well-formed <see cref="BotEntity"/>
    /// is <see cref="AuthoringShape.CliCopilot"/> when it carries a typed <c>AgentSettings</c>
    /// block or a <c>cliagent-</c> template prefix; any other well-formed entity is
    /// <see cref="AuthoringShape.Classic"/>. The <c>template</c> field is a gallery template name
    /// (e.g. <c>default-</c>, <c>websiteqna-</c>, <c>empty-</c>), not an authoring shape, so it is
    /// never used to fail an agent closed (issue #292). <see cref="AuthoringShape.Unknown"/> is
    /// reserved for the cases with no usable shape evidence: no <see cref="BotEntity"/>
    /// (<see cref="AgentClassification.None"/>) or an unreadable local workspace on the layout-only
    /// path.
    /// </remarks>
    public static class AgentClassifier
    {
        private const string CliTemplatePrefix = "cliagent-";
        private const string ClassicSettingsFileName = "settings.mcs.yml";

        /// <summary>
        /// Forward-looking, layout-only workspace marker (TDD D29). Generic-YAML
        /// <c>.sync.yaml</c> family (never MCS-parsed); carries <c>layoutVersion: &lt;int&gt;</c>
        /// and nothing else. It is layout-authoritative, NEVER shape-authoritative:
        /// <see cref="AuthoringShape"/> is always derived from <c>settings.mcs.yml</c>
        /// content, and a marker that claims the CLI layout over non-CLI content fails
        /// closed (see <see cref="DetectWorkspaceLayout"/>).
        /// </summary>
        public const string WorkspaceLayoutMarkerFileName = "agent.sync.yaml";

        /// <summary>
        /// The current (highest known) <c>layoutVersion</c>. The CLI three-layer
        /// <c>.mcs.yml</c> layout is version 1. Bump ONLY on a layout/extension-breaking
        /// change (folder moves, extension changes, entity-file changes), never on
        /// projection-rule/content tweaks that relocate no files.
        /// </summary>
        public const int CurrentLayoutVersion = 1;

        /// <summary>
        /// Parses the <c>layoutVersion</c> integer out of an <c>agent.sync.yaml</c> marker
        /// body. The marker is generic YAML (not an OM type), so this is a minimal,
        /// dependency-free line scan: the first top-level <c>layoutVersion:</c> key whose
        /// value is a non-negative integer. Returns <c>null</c> when absent/malformed.
        /// </summary>
        public static int? TryParseLayoutVersion(string? markerText)
        {
            if (string.IsNullOrEmpty(markerText))
            {
                return null;
            }

            using var reader = new StringReader(markerText!);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0 || line[0] == ' ' || line[0] == '\t' || line[0] == '#')
                {
                    continue;
                }

                const string Key = "layoutVersion:";
                if (!line.StartsWith(Key, StringComparison.Ordinal))
                {
                    continue;
                }

                var value = line.Substring(Key.Length);
                var commentIdx = value.IndexOf('#');
                if (commentIdx >= 0)
                {
                    value = value.Substring(0, commentIdx);
                }

                if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var version))
                {
                    return version;
                }

                return null;
            }

            return null;
        }

        /// <summary>
        /// Resolves the <see cref="AuthoringShape"/> of a cloud agent definition.
        /// </summary>
        public static AuthoringShape DetectAuthoringShape(DefinitionBase? definition)
            => ClassifyCloud(definition).AuthoringShape;

        /// <summary>
        /// Resolves the <see cref="AuthoringShape"/> of a cloud bot entity.
        /// </summary>
        public static AuthoringShape DetectAuthoringShape(BotEntity? bot)
            => ClassifyCloud(bot).AuthoringShape;

        /// <summary>
        /// Full structured classification from a cloud agent definition.
        /// </summary>
        public static AgentClassification ClassifyCloud(DefinitionBase? definition)
            => ClassifyCloud((definition as BotDefinition)?.Entity);

        /// <summary>
        /// Full structured classification from a cloud bot entity.
        /// </summary>
        public static AgentClassification ClassifyCloud(BotEntity? bot)
        {
            if (bot == null)
            {
                return AgentClassification.None;
            }

            // Interim shape inference from the entity's own configuration (no first-class
            // authoring-shape signal yet). Native CLI evidence: a typed AgentSettings block marks
            // the CliCopilot shape. This deliberately avoids bot.IsCliAgent(), which also returns
            // true for a legacy CLIAgentRecognizer.
            if (bot.Configuration?.AgentSettings != null)
            {
                return AgentClassification.Recognized(
                    AuthoringShape.CliCopilot, WorkspaceLayout.Unknown, "cli-agent-settings-shape");
            }

            var template = bot.Template;
            if (!string.IsNullOrEmpty(template) &&
                template!.StartsWith(CliTemplatePrefix, StringComparison.OrdinalIgnoreCase))
            {
                // CLI agent whose AgentSettings block is not (yet) materialized: the cliagent-
                // template is an explicit CLI marker, so it still resolves to CliCopilot.
                return AgentClassification.Recognized(
                    AuthoringShape.CliCopilot, WorkspaceLayout.Unknown, "template-prefix:" + template);
            }

            // No native CLI evidence. A well-formed BotEntity is a classic (Dataverse-authored)
            // agent. The template is a gallery template name (default-, websiteqna-, empty-, ...),
            // not an authoring shape, so a non-CLI template is never used to fail the agent closed
            // (issue #292): classic is the correct shape and keeps push/reattach available.
            return AgentClassification.Recognized(
                AuthoringShape.Classic, WorkspaceLayout.Unknown,
                string.IsNullOrEmpty(template) ? "classic-no-template" : "classic-template:" + template);
        }

        /// <summary>
        /// Resolves the <see cref="WorkspaceLayout"/> of a local agent folder. Both
        /// classic and CLI agents store the entity in <c>settings.mcs.yml</c> (D22), so
        /// the layout is discriminated by content (D25): the typed <c>AgentSettings</c>
        /// block marks the CLI shape, otherwise it is classic.
        /// </summary>
        public static WorkspaceLayout DetectWorkspaceLayout(string agentFolder)
        {
            if (string.IsNullOrEmpty(agentFolder))
            {
                return WorkspaceLayout.Unknown;
            }

            var contentLayout = DetectWorkspaceLayoutFromContent(agentFolder);

            // Forward-looking marker (D29). Present + KNOWN version is authoritative for
            // the LAYOUT axis, but NEVER shape-authoritative: the content shape must
            // corroborate, otherwise a marker claiming the CLI layout over non-CLI content
            // (tamper/corruption) FAILS CLOSED (Unknown). An unknown/malformed/higher
            // version falls through to best-effort content inference here; the write/pack
            // path fails closed on unknown-higher separately.
            var markerPath = Path.Combine(agentFolder, WorkspaceLayoutMarkerFileName);
            if (File.Exists(markerPath))
            {
                int? version;
                try
                {
                    version = TryParseLayoutVersion(File.ReadAllText(markerPath));
                }
                catch
                {
                    version = null;
                }

                if (version == CurrentLayoutVersion)
                {
                    return contentLayout == WorkspaceLayout.CliLayered
                        ? WorkspaceLayout.CliLayered
                        : WorkspaceLayout.Unknown;
                }
            }

            return contentLayout;
        }

        /// <summary>
        /// Node P content inference for the workspace layout (D25): both shapes store the
        /// entity in <c>settings.mcs.yml</c>, so the layout is discriminated by content -
        /// a typed <c>AgentSettings</c> block marks the CLI layout, otherwise classic.
        /// This is the transition fallback used when the <c>agent.sync.yaml</c> marker is
        /// absent or carries an unknown version.
        /// </summary>
        private static WorkspaceLayout DetectWorkspaceLayoutFromContent(string agentFolder)
        {
            var settingsPath = Path.Combine(agentFolder, ClassicSettingsFileName);
            if (!File.Exists(settingsPath))
            {
                return WorkspaceLayout.Unknown;
            }

            try
            {
                var entity = CodeSerializer.Deserialize<BotEntity>(File.ReadAllText(settingsPath));
                if (entity != null && DetectAuthoringShape(entity) == AuthoringShape.CliCopilot)
                {
                    return WorkspaceLayout.CliLayered;
                }
            }
            catch
            {
                // Unreadable/malformed settings.mcs.yml falls through to classic; the
                // per-operation support gate handles unusable workspaces elsewhere.
            }

            return WorkspaceLayout.ClassicMcs;
        }

        /// <summary>
        /// Maps a <see cref="WorkspaceLayout"/> to the authoring shape it was authored as.
        /// Used on the create/reattach path, where the local layout is the only available
        /// evidence for an agent that does not yet exist in the cloud.
        /// </summary>
        public static AuthoringShape InferAuthoringShape(WorkspaceLayout layout) => layout switch
        {
            WorkspaceLayout.CliLayered => AuthoringShape.CliCopilot,
            WorkspaceLayout.ClassicMcs => AuthoringShape.Classic,
            _ => AuthoringShape.Unknown,
        };

        /// <summary>
        /// Resolves the intended <see cref="AuthoringShape"/> from a local workspace folder
        /// via its <see cref="WorkspaceLayout"/>.
        /// </summary>
        public static AuthoringShape DetectAuthoringShapeFromFolder(string agentFolder)
            => InferAuthoringShape(DetectWorkspaceLayout(agentFolder));

        /// <summary>
        /// Combined classification from a local/cloud <see cref="DefinitionBase"/> plus a
        /// local workspace folder. A <see cref="BotComponentCollectionDefinition"/> (e.g. a
        /// sub-agent / component-collection root) is a structurally recognized format - not an
        /// authoring shape - and frequently has no <see cref="BotEntity"/> and no
        /// <c>settings.mcs.yml</c>; routing it through the entity/layout path would wrongly
        /// resolve it to <see cref="SupportLevel.Unsupported"/> and fail-close a reattach/push
        /// the handlers otherwise support. Treat it as <see cref="SupportLevel.Supported"/>
        /// directly. A <see cref="BotDefinition"/> (or null) defers to the entity-based
        /// <see cref="Classify(BotEntity?, string?)"/>.
        /// </summary>
        public static AgentClassification Classify(DefinitionBase? definition, string? agentFolder)
        {
            if (definition is BotComponentCollectionDefinition)
            {
                var layout = string.IsNullOrEmpty(agentFolder)
                    ? WorkspaceLayout.Unknown
                    : DetectWorkspaceLayout(agentFolder!);

                return new AgentClassification(
                    AuthoringShape.Unknown, layout, SupportLevel.Supported, null, "component-collection");
            }

            return Classify((definition as BotDefinition)?.Entity, agentFolder);
        }

        /// <summary>
        /// Combined classification from cloud evidence (optional) plus a local workspace
        /// folder. Cloud authoring-shape evidence is authoritative; the workspace layout
        /// fills in the layout field and provides a fallback authoring shape (and raw
        /// value) when the cloud evidence is absent or unrecognized.
        /// </summary>
        public static AgentClassification Classify(BotEntity? cloudEntity, string? agentFolder)
        {
            var layout = string.IsNullOrEmpty(agentFolder)
                ? WorkspaceLayout.Unknown
                : DetectWorkspaceLayout(agentFolder!);

            var cloud = ClassifyCloud(cloudEntity);

            if (cloud.AuthoringShape != AuthoringShape.Unknown)
            {
                return cloud.WithLayout(layout);
            }

            // The only Unknown cloud verdict is "no cloud entity" (None, no raw shape value): the
            // create/reattach path for an agent that does not yet exist in the cloud. Infer the
            // shape from the local workspace layout so a classic/CLI folder is Supported; an
            // unreadable workspace (Unknown layout) stays Unknown/Unsupported.
            if (cloud.RawShapeValue == null)
            {
                var inferred = InferAuthoringShape(layout);
                if (inferred != AuthoringShape.Unknown)
                {
                    return AgentClassification.Recognized(inferred, layout, "workspace-layout:" + layout);
                }
            }

            // No cloud entity and no readable local workspace: remain Unknown/Unsupported per the
            // cloud verdict.
            return cloud.WithLayout(layout);
        }
    }
}
