// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/Sync/ReferenceTracker.cs

using Microsoft.Agents.ObjectModel;

using Microsoft.CopilotStudio.McsCore;
namespace Microsoft.CopilotStudio.Sync;

/// <summary>
/// Provide filepaths to resolve a cross-workspace reference.
/// Map from Schema Ids (which are used in the source files) to the workspace that defines them.
/// This should be a short-lived object just for the lifetime of a clone operation.
/// </summary>
public class ReferenceTracker
{
    private readonly Dictionary<BotComponentCollectionSchemaName, DirectoryPath> _paths = new Dictionary<BotComponentCollectionSchemaName, DirectoryPath>();

    public void MarkDeclaration(BotComponentCollectionSchemaName id, DirectoryPath path)
    {
        path.EnsureIsRooted();

        _paths.Add(id, path);
    }

    public bool TryGetComponentCollection(
        BotComponentCollectionSchemaName id,
        out DirectoryPath path)
    {
        return _paths.TryGetValue(id, out path);
    }
}
