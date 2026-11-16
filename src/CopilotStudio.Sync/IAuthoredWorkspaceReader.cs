// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Reads an agent workspace as its author left it, for hosts that package it without pushing it.</summary>
public interface IAuthoredWorkspaceReader
{
    /// <summary>Reads and validates an agent workspace's local files, keeping the cloud-only metadata from the cloud cache, writing nothing, and rejecting component-collection workspaces.</summary>
    Task<AuthoredWorkspace> ReadAuthoredWorkspaceAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken);
}

/// <summary>The agent definition a workspace's files author, with the path of each knowledge file's content, whether or not that content is present.</summary>
public sealed record AuthoredWorkspace(DefinitionBase Definition, ImmutableArray<KnowledgeFileInfo> KnowledgeFiles);
