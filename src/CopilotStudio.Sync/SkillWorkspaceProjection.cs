// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

internal static class SkillWorkspaceProjection
{
    internal static DefinitionBase ResolveSkillsFromDisk(IFileAccessor fileAccessor, DefinitionBase definition, string botName, DefinitionBase? cloudSnapshot, Func<BotComponentBase, string?> skillFolderResolver, HashSet<string> existingSchemaNames)
    {
        var skills = definition.Components.OfType<DialogComponent>().Where(component => component.Dialog is InlineAgentSkill).ToList();
        if (skills.Count == 0)
        {
            return definition;
        }

        var components = definition.Components.ToList();
        var changed = false;

        foreach (var skill in skills)
        {
            var folderName = skillFolderResolver(skill);
            if (folderName == null)
            {
                continue;
            }

            changed |= ApplySkill(fileAccessor, components, skill, folderName, botName, cloudSnapshot, existingSchemaNames);
        }

        return changed ? definition.WithComponents(components.ToImmutableArray()) : definition;
    }

    private static bool ApplySkill(IFileAccessor fileAccessor, List<BotComponentBase> components, DialogComponent skill, string folderName, string botName, DefinitionBase? cloudSnapshot, HashSet<string> existingSchemaNames)
    {
        var metadata = SkillLayout.ReadAnchorMetadata(fileAccessor, folderName);
        var localManifest = SkillBodyProjection.FindManifestComponent(components, skill);
        var cloudManifest = localManifest ?? FindCloudManifest(cloudSnapshot, skill, metadata);
        var bundle = metadata.Bundle ?? SkillLayout.TryGetBundleFromMarker((skill.Dialog as InlineAgentSkill)?.Content);

        if (localManifest == null && cloudManifest == null && SkillLayout.HasAssets(fileAccessor, folderName) && SkillLayout.HasManifestFile(fileAccessor, folderName))
        {
            bundle ??= SkillLayout.MintBundleSchemaName(folderName, botName);
            ReplaceContent(components, skill, SkillLayout.BuildBundleMarker(bundle));
            components.Add(CreateNewManifestComponent(skill, folderName, botName, metadata, existingSchemaNames));
            return true;
        }

        if (localManifest == null && cloudManifest == null && bundle != null && SkillLayout.HasManifestFile(fileAccessor, folderName))
        {
            components.Add(CreateNewManifestComponent(skill, folderName, botName, metadata, existingSchemaNames));
            return true;
        }

        var changed = ReplaceContent(components, skill, SkillLayout.ResolveContent(fileAccessor, folderName, (skill.Dialog as InlineAgentSkill)?.Content, cloudManifest != null));

        if (localManifest != null || cloudManifest == null || !SkillLayout.HasManifestFile(fileAccessor, folderName))
        {
            return changed;
        }

        var builder = cloudManifest.ToBuilder();
        builder.ParentBotComponentId = skill.Id;
        if (!builder.Id.HasValue || builder.Id.Value == Guid.Empty)
        {
            builder.Id = Guid.NewGuid();
        }

        components.Add(builder.Build());
        return true;
    }

    private static FileAttachmentComponent CreateNewManifestComponent(DialogComponent skill, string folderName, string botName, McsMetadata metadata, HashSet<string> existingSchemaNames)
    {
        var schemaName = metadata.ManifestSchemaName ?? SchemaNameGenerator.GenerateSchemaNameForBotComponent(
            botSchemaPrefix: botName,
            componentPrefix: "file",
            componentDisplayName: $"{folderName}.{SkillLayout.ManifestFileName}",
            existingSchemaNames: existingSchemaNames);
        existingSchemaNames.Add(schemaName);

        var builder = new FileAttachmentComponent().WithSchemaName(schemaName).WithDisplayName(SkillLayout.ManifestFileName).ToBuilder();
        builder.Id = Guid.NewGuid();
        builder.ParentBotComponentId = skill.Id;
        return builder.Build();
    }

    private static FileAttachmentComponent? FindCloudManifest(DefinitionBase? cloudSnapshot, DialogComponent skill, McsMetadata metadata)
    {
        if (!string.IsNullOrEmpty(metadata.ManifestSchemaName))
        {
            return cloudSnapshot?.TryGetComponentBySchemaName(metadata.ManifestSchemaName!, out var declared) == true && declared is FileAttachmentComponent declaredManifest
                ? declaredManifest
                : (FileAttachmentComponent)new FileAttachmentComponent().WithSchemaName(metadata.ManifestSchemaName!).WithDisplayName(SkillLayout.ManifestFileName);
        }

        return cloudSnapshot == null || string.IsNullOrEmpty(skill.SchemaNameString) || !cloudSnapshot.TryGetComponentBySchemaName(skill.SchemaNameString!, out var cloudSkill)
            ? null
            : SkillBodyProjection.FindManifestComponent(cloudSnapshot.Components, cloudSkill);
    }

    private static bool ReplaceContent(List<BotComponentBase> components, DialogComponent skill, string? content)
    {
        if (content == null || skill.Dialog is not InlineAgentSkill inlineSkill || string.Equals(inlineSkill.Content, content, StringComparison.Ordinal))
        {
            return false;
        }

        var index = components.IndexOf(skill);
        if (index < 0)
        {
            return false;
        }

        var skillBuilder = inlineSkill.ToBuilder();
        skillBuilder.Content = content;
        var componentBuilder = skill.ToBuilder();
        componentBuilder.Dialog = skillBuilder;
        components[index] = componentBuilder.Build();
        return true;
    }
}
