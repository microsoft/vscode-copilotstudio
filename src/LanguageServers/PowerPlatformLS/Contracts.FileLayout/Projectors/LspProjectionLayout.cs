namespace Microsoft.PowerPlatformLS.Contracts.FileLayout
{
    using Microsoft.CopilotStudio.McsCore;
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// MCS layout for the Language Server, exposing computed layout maps from <see cref="LspProjection"/>.
    /// </summary>
    /// <remarks>
    /// <para>This class is a thin wrapper that exposes layout data defined in <see cref="LspProjection"/>.
    /// All layout configuration should be modified in LspProjection.cs, not here.</para>
    /// </remarks>
    public static class LspProjectionLayout
    {
        public static readonly AgentFilePath CollectionMcsYml = new AgentFilePath("collection.mcs.yml");

        /// <summary>
        /// Folder → element types mapping for file structure validation.
        /// </summary>
        /// <remarks>
        /// <para>Data source: <see cref="LspProjection.FolderToElementTypes"/></para>
        /// </remarks>
        public static readonly IReadOnlyDictionary<string, IReadOnlyCollection<Type>> FileStructureMap;

        /// <summary>
        /// Element type → folder candidates mapping (reverse lookup).
        /// </summary>
        /// <remarks>
        /// <para>Data source: <see cref="LspProjection.ElementTypeToFolders"/></para>
        /// </remarks>
        public static readonly IReadOnlyDictionary<Type, IReadOnlyCollection<string>> TypeToFileCandidates;

        static LspProjectionLayout()
        {
            // Wrap the frozen dictionaries from LspProjection as IReadOnlyCollection for API compatibility
            FileStructureMap = LspProjection.FolderToElementTypes
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyCollection<Type>)kvp.Value,
                    StringComparer.OrdinalIgnoreCase);

            TypeToFileCandidates = LspProjection.ElementTypeToFolders
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyCollection<string>)kvp.Value);
        }
    }
}

