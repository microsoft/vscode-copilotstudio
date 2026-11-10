// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Writes a workspace's cloud cache from fresh reads of the cloud, without touching the workspace's content or writing anything to the cloud.</summary>
public interface IWorkspaceCloudCacheWriter
{
    /// <summary>Replaces the sync state in the workspace's .mcs folder with the bot definition, change token, reference baseline, AI prompt baselines and .gitignore that fresh reads of the agent or component collection the operation context names give.</summary>
    /// <param name="workspaceFolder">The workspace whose cloud cache is written.</param>
    /// <param name="operationContext">The agent or component collection to read.</param>
    /// <param name="dataverseClient">The client, already targeting the asset's environment.</param>
    /// <param name="syncInfo">The asset's sync info.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    Task WriteCloudCacheAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient, AgentSyncInfo syncInfo, CancellationToken cancellationToken);
}
