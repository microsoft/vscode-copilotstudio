// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class SkillLayoutMigration
{
    internal static void Migrate(IFileAccessor fileAccessor, DefinitionBase definition, Func<BotComponentBase, AgentFilePath> pathResolver, Action<BotComponentBase> writeComponent)
    {
        if (!HasLegacyArtifacts(fileAccessor))
        {
            return;
        }

        var assetsBySkill = definition.Components.OfType<FileAttachmentComponent>()
            .Where(asset => asset.ParentBotComponentId.HasValue && !string.IsNullOrEmpty(asset.DisplayName))
            .ToLookup(asset => asset.ParentBotComponentId!.Value);

        foreach (var component in definition.Components)
        {
            if (component is DialogComponent { RootElement: AgentDialog })
            {
                MigrateChildAgent(fileAccessor, pathResolver(component), component, writeComponent);
            }
            else if (component is DialogComponent { Dialog: InlineAgentSkill } skill)
            {
                MigrateSkill(fileAccessor, skill, assetsBySkill[skill.Id.Value], pathResolver, writeComponent);
            }
        }
    }

    private static bool HasLegacyArtifacts(IFileAccessor fileAccessor)
    {
        foreach (var file in fileAccessor.ListFiles(LspProjection.BehaviorsFolder))
        {
            var remainder = file.ToString().Substring(LspProjection.BehaviorsFolder.Length);
            var slash = remainder.IndexOf('/');
            if (slash < 0)
            {
                if (remainder.EndsWith(SkillLayout.SidecarExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(remainder.Substring(slash + 1), SkillLink.LinkFileName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return fileAccessor.ListFiles(ChildAgentLink.AgentsFolderName, ChildAgentLink.LinkFileName).Any();
    }

    private static void MigrateChildAgent(IFileAccessor fileAccessor, AgentFilePath agentDefinitionPath, BotComponentBase component, Action<BotComponentBase> writeComponent)
    {
        if (!ChildAgentLink.TryGetFolderName(agentDefinitionPath.ToString(), out var folderName))
        {
            return;
        }

        var legacyLinkPath = ChildAgentLink.GetLegacyLinkPath(folderName);
        if (!fileAccessor.Exists(legacyLinkPath))
        {
            return;
        }

        writeComponent(component);
        fileAccessor.Delete(legacyLinkPath);
    }

    private static void MigrateSkill(IFileAccessor fileAccessor, DialogComponent skill, IEnumerable<FileAttachmentComponent> assets, Func<BotComponentBase, AgentFilePath> pathResolver, Action<BotComponentBase> writeComponent)
    {
        if (!SkillLink.TryGetSkillName(pathResolver(skill), out var folderName))
        {
            return;
        }

        var legacyAnchorPath = SkillLayout.GetLegacyAnchorPath(folderName);
        if (fileAccessor.Exists(legacyAnchorPath) || !fileAccessor.Exists(SkillLayout.GetAnchorPath(folderName)))
        {
            writeComponent(skill);
        }

        fileAccessor.Delete(legacyAnchorPath);
        fileAccessor.Delete(SkillLayout.GetLegacyLinkPath(folderName));

        var flattenedSidecars = ReadFlattenedSidecars(fileAccessor, folderName);
        foreach (var asset in assets)
        {
            var hasSidecar = !SkillLayout.IsManifest(asset.DisplayName);
            var sidecarPath = hasSidecar ? SkillLayout.GetAssetSidecarPath(folderName, asset.DisplayName!) : default;
            if (hasSidecar && !fileAccessor.Exists(sidecarPath))
            {
                writeComponent(asset);
            }

            var assetName = SkillLayout.NormalizeRelativeName(asset.DisplayName!);
            foreach (var stale in flattenedSidecars.Where(entry => string.Equals(entry.ComponentName, assetName, StringComparison.OrdinalIgnoreCase) && !(hasSidecar && entry.Path.Equals(sidecarPath))))
            {
                fileAccessor.Delete(stale.Path);
            }
        }
    }

    private static List<(AgentFilePath Path, string? ComponentName)> ReadFlattenedSidecars(IFileAccessor fileAccessor, string folderName)
    {
        var folderPrefix = $"{SkillLayout.GetSkillFolderPath(folderName)}/";
        var sidecars = new List<(AgentFilePath, string?)>();

        foreach (var candidate in fileAccessor.ListFiles(SkillLayout.GetSkillFolderPath(folderName), "*" + SkillLayout.SidecarExtension))
        {
            if (candidate.ToString().IndexOf('/', folderPrefix.Length) < 0)
            {
                // The anchor lives at the skill folder root and carries the skill's componentName,
                // so an asset whose relative path equals the skill display name would otherwise
                // match it and delete the skill's identity file as a stale sidecar.
                if (string.Equals(candidate.ToString().Substring(folderPrefix.Length), SkillLayout.AnchorFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var componentName = SkillLayout.ReadMetadata(fileAccessor, candidate).ComponentName;
                sidecars.Add((candidate, componentName == null ? null : SkillLayout.NormalizeRelativeName(componentName)));
            }
        }

        return sidecars;
    }
}
