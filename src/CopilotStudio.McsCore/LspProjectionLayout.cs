// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Contracts.FileLayout/Projectors/LspProjectionLayout.cs

using System.Linq;


namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// MCS layout for the Language Server, exposing computed layout maps from <see cref="LspProjection"/>.
/// </summary>
internal static class LspProjectionLayout
{
    public static readonly AgentFilePath CollectionMcsYml = new AgentFilePath("collection.mcs.yml");

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
}
