// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.McsCore
{
    /// <summary>Maps the per-file change schema names of workflows, custom connectors and AI prompts to the item that owns them.</summary>
    public static class SyncedAssetSchemaName
    {
        private const int GuidLength = 36;

        private static readonly string[] SchemaNamePrefixes = { WorkflowLayout.SchemaNamePrefix, CustomConnectorLayout.SchemaNamePrefix, AiPromptLayout.SchemaNamePrefix };

        /// <summary>Gets the schema name of the workflow, custom connector or AI prompt that owns a file change, or the schema name itself for any other change.</summary>
        /// <param name="schemaName">The change schema name, such as <c>Mcs.Connector.{id}.openapidefinition</c>.</param>
        public static string GetAssetSchemaName(string schemaName) => SchemaNamePrefixes.FirstOrDefault(prefix => schemaName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } prefix && schemaName.Length > prefix.Length + GuidLength && schemaName[prefix.Length + GuidLength] == '.' && Guid.TryParse(schemaName.Substring(prefix.Length, GuidLength), out _) ? schemaName.Substring(0, prefix.Length + GuidLength) : schemaName;
    }
}
