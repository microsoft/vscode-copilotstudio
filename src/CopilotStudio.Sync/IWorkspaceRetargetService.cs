// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using System.Collections.Immutable;

namespace Microsoft.CopilotStudio.Sync;

public interface IWorkspaceRetargetService
{
    /// <summary>
    /// Captures and clears the environment-specific remote binding state.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace.</param>
    /// <returns>A snapshot of the cleared remote binding state.</returns>
    RemoteBindingSnapshot ResetRemoteBindingState(DirectoryPath workspaceFolder);

    /// <summary>
    /// Restores remote binding state previously captured by <see cref="ResetRemoteBindingState"/>.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace.</param>
    /// <param name="snapshot">The binding snapshot to restore.</param>
    void RestoreRemoteBindingState(DirectoryPath workspaceFolder, RemoteBindingSnapshot snapshot);

    void PersistRetargetBackup(DirectoryPath workspaceFolder, RemoteBindingSnapshot snapshot);

    bool FinalizeRetarget(DirectoryPath workspaceFolder, bool pushSucceeded);

    void ClearRetargetBackup(DirectoryPath workspaceFolder);

    /// <summary>
    /// Sync workspace using AI prompt metadata already upserted to the target environment during retarget.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="operationContext">Context.</param>
    /// <param name="changeToken">Change token.</param>
    /// <param name="updateWorkspaceDirectory">Whether to update workspace directory.</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="syncInfo">Synchronization information for the agent.</param>
    /// <param name="cloudFlowMetadata">Cloud flow metadata to be written to workspace during sync.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <param name="aiPromptMetadata">AI prompt metadata already upserted to the target environment.</param>
    /// <param name="syncCustomConnectors">When false, skips custom connector folder reconciliation.</param>
    /// <returns>Workspace sync result.</returns>
    Task<WorkspaceSyncInfo> SyncWorkspaceAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        string? changeToken,
        bool updateWorkspaceDirectory,
        ISyncDataverseClient dataverseClient,
        AgentSyncInfo syncInfo,
        CloudFlowMetadata? cloudFlowMetadata,
        CancellationToken cancellationToken,
        ImmutableArray<AIPromptMetadata> aiPromptMetadata,
        bool syncCustomConnectors = true);
}
