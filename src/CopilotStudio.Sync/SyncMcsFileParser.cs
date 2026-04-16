// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.FileProjection;

using Microsoft.CopilotStudio.McsCore;
namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Thin adapter for sync operations. Delegates to <see cref="McsFileParserCore"/>.
/// Wraps errors as <see cref="InvalidOperationException"/>.
/// </summary>
internal class SyncMcsFileParser : IMcsFileParser
{
    private readonly LspProjectorService _projectorService;

    public SyncMcsFileParser(LspProjectorService projectorService)
    {
        _projectorService = projectorService;
    }

    public (BotComponentBase? component, Exception? error) CompileFileModel(string schemaName, BotElement? model, string? displayName = null, string? description = null)
    {
        return McsFileParserCore.InternalCompileFile(
            _projectorService, McsFileParserCore.VirtualPath, schemaName, model,
            (msg, _) => new InvalidOperationException(msg), displayName, description);
    }

    public (BotComponentBase? component, Exception? error) CompileFile(AgentFilePath relativePath, BotElement fileModel, ProjectionContext context)
    {
        if (fileModel == null)
        {
            return (null, new InvalidDataException($"File model is null for {relativePath}"));
        }

        var schemaName = McsFileParserCore.DeriveSchemaName(_projectorService, fileModel, relativePath, context);

        if (schemaName == null)
        {
            return (null, new InvalidOperationException($"Can't get schema: {fileModel.GetType().Name}"));
        }

        try
        {
            var (displayName, description) = McsFileParserCore.GetMetaDataInfo(fileModel, schemaName);
            return McsFileParserCore.InternalCompileFile(
                _projectorService, relativePath, schemaName, fileModel,
                (msg, _) => new InvalidOperationException(msg), displayName, description);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }
}
