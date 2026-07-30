// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore
{
    internal static class SkillLink
    {
        internal const string LinkFileName = ".skill.json";
        internal const string CompoundExtension = ".mcs.yml";

        internal static IReadOnlyDictionary<string, string> ReadSchemaLinks(IFileAccessor fileAccessor, BotDefinition? cloudDefinition = null, bool throwOnInvalidLink = false)
        {
            var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var skillFilePath in fileAccessor.ListFiles(LspProjection.BehaviorsFolder, "*" + CompoundExtension))
            {
                if (!TryGetSkillName(skillFilePath, out var skillName))
                {
                    continue;
                }

                if (TryResolveSchemaName(fileAccessor, skillName, cloudDefinition, throwOnInvalidLink, out var schemaName, out _))
                {
                    links[skillName] = schemaName;
                }
            }

            return links;
        }

        internal static bool TryResolveSchemaName(IFileAccessor fileAccessor, string skillName, BotDefinition? cloudDefinition, bool throwOnInvalidLink, out string schemaName, out DialogComponent? cloudSkill)
        {
            schemaName = string.Empty;
            cloudSkill = null;
            var cloudSkills = cloudDefinition?.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill && !string.IsNullOrEmpty(component.SchemaNameString)).GroupBy(component => component.SchemaNameString!, StringComparer.Ordinal).Select(group => group.Last()).ToList() ?? new List<DialogComponent>();
            var link = SchemaLink.TryRead(fileAccessor, new AgentFilePath($"{LspProjection.BehaviorsFolder}{skillName}/{LinkFileName}"));

            if (link != null && (!string.Equals(link.FolderName, skillName, StringComparison.Ordinal) || string.IsNullOrEmpty(link.SchemaName)))
            {
                if (throwOnInvalidLink)
                {
                    throw new InvalidOperationException($"The packaged skill folder 'behaviors/{skillName}' has an invalid '{LinkFileName}' link. Restore the original folder name or get the latest changes.");
                }

                link = null;
            }

            if (link != null)
            {
                cloudSkill = cloudSkills.SingleOrDefault(component => string.Equals(component.SchemaNameString, link.SchemaName, StringComparison.Ordinal));
                if (cloudSkill != null || (cloudSkills.Count == 0 && IsSchemaForBot(link.SchemaName, cloudDefinition)))
                {
                    schemaName = link.SchemaName;
                    return true;
                }
            }

            cloudSkill = MatchCloudSkill(skillName, cloudDefinition, cloudSkills, throwOnInvalidLink);
            if (cloudSkill != null)
            {
                schemaName = cloudSkill.SchemaNameString!;
                return true;
            }

            if (link != null && throwOnInvalidLink)
            {
                throw new InvalidOperationException($"The packaged skill folder 'behaviors/{skillName}' links to '{link.SchemaName}', but no matching cloud skill was found. Get the latest changes or re-clone the agent.");
            }

            return false;
        }

        private static bool IsSchemaForBot(string schemaName, BotDefinition? cloudDefinition)
        {
            if (cloudDefinition == null)
            {
                return true;
            }

            var botName = cloudDefinition.Entity?.SchemaName.Value;
            return !string.IsNullOrEmpty(botName) && schemaName.StartsWith($"{botName}.skill.", StringComparison.Ordinal);
        }

        private static DialogComponent? MatchCloudSkill(string skillName, BotDefinition? cloudDefinition, IReadOnlyCollection<DialogComponent> cloudSkills, bool throwOnAmbiguousMatch)
        {
            var botName = cloudDefinition?.Entity?.SchemaName.Value;
            if (!string.IsNullOrEmpty(botName))
            {
                var derivedSchema = LspProjection.GetSchemaName($"{LspProjection.BehaviorsFolder}{skillName}", botName, typeof(InlineAgentSkill), AuthoringShape.CliCopilot);
                var exactSchemaMatch = cloudSkills.SingleOrDefault(component => string.Equals(component.SchemaNameString, derivedSchema, StringComparison.Ordinal));
                if (exactSchemaMatch != null)
                {
                    return exactSchemaMatch;
                }
            }

            var displayMatches = cloudSkills.Where(component => string.Equals(SubAgentFolderNaming.FromDisplayName(component.DisplayName, keepSpaces: true), skillName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (displayMatches.Count == 1)
            {
                return displayMatches[0];
            }

            if (displayMatches.Count > 1 && throwOnAmbiguousMatch)
            {
                throw new InvalidOperationException($"The packaged skill folder 'behaviors/{skillName}' matches multiple cloud skills. Get the latest changes or rename the local skill folder.");
            }

            return null;
        }

        internal static bool HasSidecarLink(IFileAccessor fileAccessor, string skillName) => fileAccessor.Exists(new AgentFilePath($"{LspProjection.BehaviorsFolder}{skillName}/{LinkFileName}"));

        internal static bool HasAnchorFile(IFileAccessor fileAccessor, string skillName) => fileAccessor.Exists(new AgentFilePath($"{LspProjection.BehaviorsFolder}{skillName}{CompoundExtension}"));

        internal static bool TryGetSkillName(AgentFilePath skillFilePath, out string skillName)
        {
            skillName = string.Empty;
            var pathValue = skillFilePath.ToString();
            if (!pathValue.StartsWith(LspProjection.BehaviorsFolder, StringComparison.Ordinal))
            {
                return false;
            }

            var remainder = pathValue.Substring(LspProjection.BehaviorsFolder.Length);
            if (remainder.IndexOf('/') >= 0 || !remainder.EndsWith(CompoundExtension, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            skillName = remainder.Substring(0, remainder.Length - CompoundExtension.Length);
            return skillName.Length > 0;
        }
    }
}
