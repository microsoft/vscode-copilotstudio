// Copyright (C) Microsoft Corporation. All rights reserved.
// PAC implementation of IMcsFileParser, extracting core logic from McsFileParser.InternalCompileFile.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Microsoft.CopilotStudio.McsCore;
namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Simplified file parser for PAC sync operations.
/// Delegates projection to <see cref="LspProjectorService"/>.
/// </summary>
internal class SyncMcsFileParser : IMcsFileParser
{
    /// <summary>
    /// This is not a real path and is used when compiling model from a virtual source.
    /// </summary>
    private static readonly AgentFilePath VirtualPath = new AgentFilePath(".mcs/virtual_content_model");

    private readonly LspProjectorService _projectorService;

    public SyncMcsFileParser(LspProjectorService projectorService)
    {
        _projectorService = projectorService;
    }

    public (BotComponentBase? component, Exception? error) CompileFileModel(string schemaName, BotElement? model, string? displayName = null, string? description = null)
    {
        return InternalCompileFile(VirtualPath, schemaName, model, displayName, description);
    }

    private (BotComponentBase? component, Exception? error) InternalCompileFile(
       AgentFilePath relativePath,
       string schemaName,
       BotElement? fileModel,
       string? displayName = null,
       string? description = null)
    {
        var subAgentId = GetSubAgentComponentId(relativePath);
        BotComponentBase component;

        if (fileModel == null)
        {
            return (null, new InvalidOperationException("Unhandled data type: model is null"));
        }

        // Handle elements that are not components or agent files
        if (fileModel is BotEntity or DefinitionBase or BotComponentCollection or
            ReferencesSourceFile or ConnectionReferencesSourceFile or EnvironmentVariableDefinition)
        {
            return (null, null);
        }

        // Look up projector for component creation
        var projector = _projectorService.GetProjector(fileModel.GetType(), relativePath.ToString());

        if (projector != null)
        {
            var normalizedModel = _projectorService.NormalizeElement(fileModel);
            component = projector.CreateComponent(
                normalizedModel,
                schemaName,
                subAgentId,
                displayName,
                description);
        }
        else
        {
            return (null, new InvalidOperationException($"Unhandled data type: {fileModel.GetType().Name}"));
        }

        if (component.SchemaNameString != schemaName)
        {
            throw new InvalidOperationException($"SchemaName not set properly: {schemaName}");
        }

        return (component, null);
    }

    public (BotComponentBase? component, Exception? error) CompileFile(AgentFilePath relativePath, BotElement fileModel, ProjectionContext context)
    {
        if (fileModel == null)
        {
            return (null, new InvalidDataException($"File model is null for {relativePath}"));
        }

        // Derive schema name using projector or element's SchemaName property
        var schemaName = DeriveSchemaName(fileModel, relativePath, context);

        if (schemaName == null)
        {
            return (null, new InvalidOperationException($"Can't get schema: {fileModel.GetType().Name}"));
        }

        try
        {
            var (displayName, description) = GetMetaDataInfo(fileModel, schemaName);
            return InternalCompileFile(relativePath, schemaName, fileModel, displayName, description);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private static (string?, string?) GetMetaDataInfo(BotElement fileModel, string? schemaName)
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
    private string? DeriveSchemaName(BotElement fileModel, AgentFilePath relativePath, ProjectionContext context)
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
        return _projectorService.GetSchemaName(pathWithoutExtension, context.BotName, fileModel.GetType());
    }

    // If this is in a /agent/ folder, then we need a component Id
    // to associate ita s a child-agent component.
    private static Guid GetSubAgentComponentId(AgentFilePath filePath)
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
    private static Guid HashStringToGuid(string input)
    {
        // Used only a single time within this class.
        using var md5 = MD5.Create(); // CodeQL [SM02196] False Positive: Not used for security purposes
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        return new Guid(hash);
    }
}
