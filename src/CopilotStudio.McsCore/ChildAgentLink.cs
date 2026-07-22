// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.CopilotStudio.McsCore
{
    internal static class ChildAgentLink
    {
        internal const string LinkFileName = ".agent.json";
        internal const string AgentDefinitionFileName = "agent.mcs.yml";
        internal const string AgentsFolderName = "agents";
        internal const string AgentsFolderPrefix = AgentsFolderName + "/";

        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
        };

        internal sealed class LinkData
        {
            [JsonPropertyName("schemaName")]
            public string SchemaName { get; set; } = string.Empty;

            [JsonPropertyName("folderName")]
            public string FolderName { get; set; } = string.Empty;
        }

        internal static LinkData? Parse(string json) => JsonSerializer.Deserialize<LinkData>(json, SerializerOptions);

        internal static string Serialize(LinkData link) => JsonSerializer.Serialize(link, SerializerOptions);

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

                var linkPath = new AgentFilePath(pathValue.Substring(0, folderEnd + 1) + LinkFileName);
                if (!fileAccessor.Exists(linkPath))
                {
                    continue;
                }

                LinkData? link = null;
                try
                {
                    using var stream = fileAccessor.OpenRead(linkPath);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    link = Parse(reader.ReadToEnd());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    link = null;
                }

                if (link != null && !string.IsNullOrEmpty(link.SchemaName))
                {
                    links[folderName] = link.SchemaName;
                }
            }

            return links;
        }
    }
}