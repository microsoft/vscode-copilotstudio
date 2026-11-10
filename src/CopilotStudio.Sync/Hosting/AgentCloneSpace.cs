// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync.Hosting;

internal interface IAgentCloneSpace
{
    ISyncDataverseClient Dataverse { get; }

    IOperationContextProvider OperationContexts { get; }

    IWorkspaceSynchronizer Synchronizer { get; }

    IFileAccessorFactory Files { get; }

    DirectoryPath Root { get; }
}

internal static class AgentCloneSpaceExtensions
{
    public static DirectoryPath FolderOf(this IAgentCloneSpace space, string folder) => space.Root.GetChildDirectoryPath(folder);

    public static IFileAccessor Open(this IAgentCloneSpace space, string folder) => space.Files.Create(space.FolderOf(folder));
}
