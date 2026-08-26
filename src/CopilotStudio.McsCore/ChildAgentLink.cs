// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore
{
    internal static class ChildAgentLink
    {
        internal const string LinkFileName = ".agent.json";
        internal const string AgentDefinitionFileName = "agent.mcs.yml";
        internal const string AgentsFolderName = "agents";
        internal const string AgentsFolderPrefix = AgentsFolderName + "/";

        internal static IReadOnlyDictionary<string, string> ReadSchemaLinks(IFileAccessor fileAccessor)
        {
            var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var agentDefinitionPath in fileAccessor.ListFiles(AgentsFolderName, AgentDefinitionFileName))
            {
                if (!TryGetFolderName(agentDefinitionPath.ToString(), out var folderName))
                {
                    continue;
                }

                var link = ReadLink(fileAccessor, agentDefinitionPath, folderName);
                if (link != null)
                {
                    links[folderName] = link.SchemaName;
                }
            }

            return links;
        }


        internal static SchemaLinkData? ReadLink(IFileAccessor fileAccessor, AgentFilePath agentDefinitionPath, string folderName)
        {
            var anchorSchemaName = SkillLayout.ReadMetadata(fileAccessor, agentDefinitionPath).SchemaName;
            if (!string.IsNullOrEmpty(anchorSchemaName))
            {
                return new SchemaLinkData { SchemaName = anchorSchemaName!, FolderName = folderName };
            }

            var link = SchemaLink.TryRead(fileAccessor, GetLegacyLinkPath(folderName));
            return string.IsNullOrEmpty(link?.SchemaName) || string.IsNullOrEmpty(link?.FolderName) ? null : link;
        }

        internal static AgentFilePath GetLegacyLinkPath(string folderName) => new AgentFilePath($"{AgentsFolderPrefix}{folderName}/{LinkFileName}");

        internal static bool TryGetFolderName(string agentDefinitionPath, out string folderName)
        {
            folderName = string.Empty;
            if (!agentDefinitionPath.StartsWith(AgentsFolderPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var folderEnd = agentDefinitionPath.IndexOf('/', AgentsFolderPrefix.Length);
            if (folderEnd < 0 || !string.Equals(agentDefinitionPath.Substring(folderEnd + 1), AgentDefinitionFileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            folderName = agentDefinitionPath.Substring(AgentsFolderPrefix.Length, folderEnd - AgentsFolderPrefix.Length);
            return folderName.Length > 0;
        }
    }
}
