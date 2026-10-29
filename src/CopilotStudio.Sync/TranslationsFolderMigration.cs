// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;

namespace Microsoft.CopilotStudio.Sync;

internal static class TranslationsFolderMigration
{
    internal static void Migrate(IFileAccessor fileAccessor, DefinitionBase definition, string botName, Func<BotComponentBase, AgentFilePath> resolveComponentPath, Action<string> reportProgress)
    {
        foreach (var component in definition.Components)
        {
            var componentPath = resolveComponentPath(component);
            if (!fileAccessor.Exists(componentPath) && TryGetLegacyPath(componentPath, component, botName, out var legacyPath) && fileAccessor.Exists(legacyPath))
            {
                fileAccessor.Replace(legacyPath, componentPath);
                reportProgress($"Moved '{legacyPath}' to '{componentPath}' to match the current workspace layout.");
            }
        }
    }

    internal static bool TryGetLegacyPath(AgentFilePath componentPath, BotComponentBase component, string? botName, out AgentFilePath legacyPath)
    {
        var path = LspProjection.IsTranslationsPath(componentPath.ToString()) || string.IsNullOrEmpty(component.SchemaNameString) ? null : LspProjection.GetFilePath(typeof(TranslationsComponent), component.SchemaNameString!, botName, componentPath.TryGetSubAgentName(out var subAgentName, out _) ? $"{LspProjection.AgentsFolder}{subAgentName}/" : null, pathWithoutExtension: null);
        legacyPath = path == null ? default : new AgentFilePath(path);
        return path != null;
    }
}
