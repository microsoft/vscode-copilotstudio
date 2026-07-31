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
                var pathValue = agentDefinitionPath.ToString().Replace('\\', '/');

                if (!pathValue.StartsWith(AgentsFolderPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var folderStart = AgentsFolderPrefix.Length;
                var folderEnd = pathValue.IndexOf('/', folderStart);
                if (folderEnd < 0)
                {
                    continue;
                }

                var folderName = pathValue.Substring(folderStart, folderEnd - folderStart);
                if (folderName.Length == 0 || pathValue.Substring(folderEnd + 1) != AgentDefinitionFileName)
                {
                    continue;
                }

                var link = SchemaLink.TryRead(fileAccessor, new AgentFilePath(pathValue.Substring(0, folderEnd + 1) + LinkFileName));
                if (link != null && !string.IsNullOrEmpty(link.SchemaName))
                {
                    links[folderName] = link.SchemaName;
                }
            }

            return links;
        }
    }
}