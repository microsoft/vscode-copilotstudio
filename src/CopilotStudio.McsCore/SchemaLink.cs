// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.CopilotStudio.McsCore
{
    internal sealed class SchemaLinkData
    {
        [JsonPropertyName("schemaName")]
        public string SchemaName { get; set; } = string.Empty;

        [JsonPropertyName("folderName")]
        public string FolderName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Shared serialization for the hidden folder to cloud schema link files.
    /// </summary>
    internal static class SchemaLink
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            WriteIndented = true,
        };

        internal static SchemaLinkData? Parse(string json) => JsonSerializer.Deserialize<SchemaLinkData>(json, SerializerOptions);

        internal static string Serialize(SchemaLinkData link) => JsonSerializer.Serialize(link, SerializerOptions);

        internal static SchemaLinkData? TryRead(IFileAccessor fileAccessor, AgentFilePath linkPath)
        {
            if (!fileAccessor.Exists(linkPath))
            {
                return null;
            }

            try
            {
                using var stream = fileAccessor.OpenRead(linkPath);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return Parse(reader.ReadToEnd());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }
    }
}
