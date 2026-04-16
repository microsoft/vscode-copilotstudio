// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.CopilotStudio.McsCore;

/// <summary>
/// Shared parsing algorithm extracted from SyncMcsFileParser and McsFileParser.
/// Both consumers delegate to these static methods through thin adapters.
/// </summary>
internal static class McsFileParserCore
{
    /// <summary>
    /// This is not a real path and is used when compiling model from a virtual source.
    /// </summary>
    internal static readonly AgentFilePath VirtualPath = new AgentFilePath(".mcs/virtual_content_model");

    internal static (BotComponentBase? component, Exception? error) InternalCompileFile(
       LspProjectorService projectorService,
       AgentFilePath relativePath,
       string schemaName,
       BotElement? fileModel,
       Func<string, BotElement?, Exception> createError,
       string? displayName = null,
       string? description = null)
    {
        var subAgentId = GetSubAgentComponentId(relativePath);
        BotComponentBase component;

        if (fileModel == null)
        {
            return (null, createError("Unhandled data type", fileModel));
        }

        // Handle elements that are not components or agent files
        if (fileModel is BotEntity or DefinitionBase or BotComponentCollection or
            ReferencesSourceFile or ConnectionReferencesSourceFile or EnvironmentVariableDefinition)
        {
            return (null, null);
        }

        // Look up projector for component creation
        var projector = projectorService.GetProjector(fileModel.GetType(), relativePath.ToString());

        if (projector != null)
        {
            var normalizedModel = projectorService.NormalizeElement(fileModel);
            component = projector.CreateComponent(
                normalizedModel,
                schemaName,
                subAgentId,
                displayName,
                description);
        }
        else
        {
            return (null, createError("Unhandled data type", fileModel));
        }

        if (component.SchemaNameString != schemaName)
        {
            throw new InvalidOperationException($"SchemaName not set properly: {schemaName}");
        }

        return (component, null);
    }

    internal static (string?, string?) GetMetaDataInfo(BotElement fileModel, string? schemaName)
    {
        string? displayName = null;
        string? description = null;
        var fallbackName = !string.IsNullOrWhiteSpace(schemaName) ? schemaName.Split('.').Last() : string.Empty;

        try
        {
            if (fileModel.ExtensionData is RecordDataValue record && record.Properties.TryGetValue("mcs.metadata", out var metadataValue) && metadataValue is RecordDataValue metadataRecord)
            {
                displayName = GetRecordString(metadataRecord, "componentName");
                description = GetRecordString(metadataRecord, "description");
            }

            CodeSerializer.ParseYamlHeader(fileModel, out var ymlDisplayName, out var ymlDescription);

            displayName ??= ymlDisplayName ?? fallbackName;
            description ??= ymlDescription;
        }
        catch
        {
            displayName ??= fallbackName;
        }

        return (displayName, description);
    }

    private static string? GetRecordString(RecordDataValue record, string key) => record.Properties.TryGetValue(key, out var value) && value is StringDataValue s ? s.Value : null;

    /// <summary>
    /// Derives schema name from file model using projector or element's SchemaName property.
    /// </summary>
    internal static string? DeriveSchemaName(LspProjectorService projectorService, BotElement fileModel, AgentFilePath relativePath, ProjectionContext context)
    {
        // Handle elements that have their own SchemaName property
        if (fileModel is BotEntity entity)
        {
            return entity.SchemaName.ToString();
        }

        if (fileModel is BotComponentCollection collection)
        {
            return collection.SchemaName.ToString();
        }

        // Handle agent files and non-component elements that return empty schema name
        if (fileModel is BotDefinition or BotComponentCollectionDefinition or UnknownBotElement
            or ReferencesSourceFile or ConnectionReferencesSourceFile or EnvironmentVariableDefinition)
        {
            return string.Empty;
        }

        // Use the projector service for schema name derivation (includes legacy special cases)
        var pathWithoutExtension = relativePath.RemoveExtension().ToString();
        return projectorService.GetSchemaName(pathWithoutExtension, context.BotName, fileModel.GetType());
    }

    // If this is in a /agent/ folder, then we need a component Id
    // to associate it as a child-agent component.
    internal static Guid GetSubAgentComponentId(AgentFilePath filePath)
    {
        if (filePath.TryGetSubAgentName(out var agentName, out _))
        {
            return HashStringToGuid(agentName);
        }

        return Guid.Empty;
    }

    // Only the cloud has real componentIds.
    // Fabricate a guid from the schema name.
    // We just need this locally when creating compilations.
    internal static Guid HashStringToGuid(string input)
    {
        // Used only a single time within this class.
        using var md5 = MD5.Create(); // CodeQL [SM02196] False Positive: Not used for security purposes
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
