// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore;

internal static class SkillBodyProjection
{
    internal static bool IsSkillAnchor(BotComponentBase component, AgentFilePath path)
        => component is DialogComponent { Dialog: InlineAgentSkill } && SkillLink.TryGetSkillName(path, out _);

    internal static bool IsImplicitManifest(BotComponentBase component, DefinitionBase definition)
        => component is FileAttachmentComponent attachment && SkillLayout.IsManifest(attachment.DisplayName) && HasSkillParent(attachment, definition);

    internal static McsMetadata GetBodyMetadata(BotComponentBase component, DefinitionBase definition, AgentFilePath path)
    {
        if (IsSkillAnchor(component, path))
        {
            var bundle = SkillLayout.TryGetBundleFromMarker((component as DialogComponent)?.Dialog is InlineAgentSkill skill ? skill.Content : null);
            return new McsMetadata(component.DisplayName, component.Description, component.SchemaNameString, bundle, bundle == null ? null : FindManifestSchemaName(component, definition));
        }

        var pathValue = path.ToString();
        if (pathValue.StartsWith(LspProjection.BehaviorsFolder, StringComparison.Ordinal) || ChildAgentLink.TryGetFolderName(pathValue, out _))
        {
            return new McsMetadata(component.DisplayName, component.Description, component.SchemaNameString, null, null);
        }

        return default;
    }

    internal static BotComponentBase PrepareForWrite(BotComponentBase component, AgentFilePath path)
    {
        if (!IsSkillAnchor(component, path) || component is not DialogComponent dialogComponent || dialogComponent.Dialog is not InlineAgentSkill skill || string.IsNullOrEmpty(skill.Content))
        {
            return component;
        }

        var skillBuilder = skill.ToBuilder();
        skillBuilder.Content = null;
        var componentBuilder = dialogComponent.ToBuilder();
        componentBuilder.Dialog = skillBuilder;
        return componentBuilder.Build();
    }

    internal static bool TryGetManifestWrite(BotComponentBase component, AgentFilePath path, out AgentFilePath manifestPath, out string manifestText)
    {
        manifestPath = default;
        manifestText = string.Empty;

        if (!IsSkillAnchor(component, path)
            || component is not DialogComponent { Dialog: InlineAgentSkill skill }
            || string.IsNullOrEmpty(skill.Content)
            || SkillLayout.TryGetBundleFromMarker(skill.Content) != null
            || !SkillLink.TryGetSkillName(path, out var folderName))
        {
            return false;
        }

        manifestPath = SkillLayout.GetManifestPath(folderName);
        manifestText = skill.Content!;
        return true;
    }

    internal static FileAttachmentComponent? FindManifestComponent(IEnumerable<BotComponentBase> components, BotComponentBase skill)
        => components.OfType<FileAttachmentComponent>().FirstOrDefault(component => component.ParentBotComponentId.HasValue && component.ParentBotComponentId.Value == skill.Id && SkillLayout.IsManifest(component.DisplayName));

    private static string? FindManifestSchemaName(BotComponentBase skill, DefinitionBase definition)
        => FindManifestComponent(definition.Components, skill)?.SchemaNameString;

    private static bool HasSkillParent(BotComponentBase component, DefinitionBase definition)
        => component.ParentBotComponentId.HasValue && definition.TryGetBotComponentById(component.ParentBotComponentId.Value, out var parent) && parent is DialogComponent { Dialog: InlineAgentSkill };
}
