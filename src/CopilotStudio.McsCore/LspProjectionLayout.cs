// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/Projectors/LspProjectionLayout.cs

using System.Linq;
using Microsoft.Agents.ObjectModel;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// MCS layout for the Language Server, exposing computed layout maps from <see cref="LspProjection"/>.
/// </summary>
internal static class LspProjectionLayout
{
    public static readonly AgentFilePath CollectionMcsYml = new AgentFilePath("collection.mcs.yml");
    private static readonly IReadOnlyCollection<Type> PackagedSkillPayloadTypes = new[] { typeof(FileAttachmentComponent) };

    public static readonly IReadOnlyDictionary<string, IReadOnlyCollection<Type>> FileStructureMap =
        LspProjection.FolderToElementTypes
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyCollection<Type>)kvp.Value,
                StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<Type, IReadOnlyCollection<string>> TypeToFileCandidates =
        LspProjection.ElementTypeToFolders
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyCollection<string>)kvp.Value);

    public static bool TryGetPackagedSkillPayloadTypes(AgentFilePath completeRelativePath, out IReadOnlyCollection<Type> types)
    {
        var segments = completeRelativePath.ToString().Split('/');
        if (segments.Length == 3 && string.Equals(segments[0] + "/", LspProjection.BehaviorsFolder, StringComparison.OrdinalIgnoreCase))
        {
            types = PackagedSkillPayloadTypes;
            return true;
        }

        types = Array.Empty<Type>();
        return false;
    }
}
