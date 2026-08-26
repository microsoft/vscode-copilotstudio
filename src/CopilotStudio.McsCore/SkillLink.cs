// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore
{
    internal static class SkillLink
    {
        internal const string LinkFileName = ".skill.json";
        internal const string CompoundExtension = ".mcs.yml";

        internal static IReadOnlyDictionary<string, string> ReadSchemaLinks(IFileAccessor fileAccessor, BotDefinition? cloudDefinition = null, bool throwOnInvalidLink = false, IReadOnlyList<string>? skillFolders = null)
        {
            var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var skillName in skillFolders ?? SkillLayout.ListSkillFolders(fileAccessor))
            {
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
            var linkedSchemaName = ReadLinkedSchemaName(fileAccessor, skillName, throwOnInvalidLink);

            if (linkedSchemaName != null)
            {
                cloudSkill = cloudSkills.SingleOrDefault(component => string.Equals(component.SchemaNameString, linkedSchemaName, StringComparison.Ordinal));
                if (cloudSkill != null || (cloudSkills.Count == 0 && IsSchemaForBot(linkedSchemaName, cloudDefinition)))
                {
                    schemaName = linkedSchemaName;
                    return true;
                }
            }

            cloudSkill = MatchCloudSkill(skillName, cloudDefinition, cloudSkills, throwOnInvalidLink);
            if (cloudSkill != null)
            {
                schemaName = cloudSkill.SchemaNameString!;
                return true;
            }

            if (linkedSchemaName != null && throwOnInvalidLink)
            {
                throw new InvalidOperationException($"The packaged skill folder 'behaviors/{skillName}' links to '{linkedSchemaName}', but no matching cloud skill was found. Get the latest changes or re-clone the agent.");
            }

            return false;
        }

        private static string? ReadLinkedSchemaName(IFileAccessor fileAccessor, string skillName, bool throwOnInvalidLink)
        {
            var anchorSchemaName = SkillLayout.ReadAnchorMetadata(fileAccessor, skillName).SchemaName;
            if (!string.IsNullOrEmpty(anchorSchemaName))
            {
                return anchorSchemaName;
            }

            var link = SchemaLink.TryRead(fileAccessor, SkillLayout.GetLegacyLinkPath(skillName));
            if (link == null)
            {
                return null;
            }

            if (!string.Equals(link.FolderName, skillName, StringComparison.Ordinal) || string.IsNullOrEmpty(link.SchemaName))
            {
                if (throwOnInvalidLink)
                {
                    throw new InvalidOperationException($"The packaged skill folder has an invalid link. Restore the original folder name or get the latest changes.");
                }

                return null;
            }

            return link.SchemaName;
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
                var derivedSchema = LspProjection.GetSchemaName(SkillLayout.GetSkillFolderPath(skillName), botName, typeof(InlineAgentSkill), AuthoringShape.CliCopilot);
                var exactSchemaMatch = cloudSkills.SingleOrDefault(component => string.Equals(component.SchemaNameString, derivedSchema, StringComparison.Ordinal));
                if (exactSchemaMatch != null)
                {
                    return exactSchemaMatch;
                }
            }

            var displayMatches = cloudSkills.Where(component => string.Equals(component.DisplayName, skillName, StringComparison.OrdinalIgnoreCase) || string.Equals(SubAgentFolderNaming.FromDisplayName(component.DisplayName, keepSpaces: true), skillName, StringComparison.OrdinalIgnoreCase)).ToList();
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

        internal static bool TryGetSkillName(AgentFilePath skillFilePath, out string skillName) => SkillLayout.TryGetFolderFromAnchorPath(skillFilePath.ToString(), out skillName);
    }
}
