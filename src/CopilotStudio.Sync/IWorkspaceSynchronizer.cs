// Copyright (C) Microsoft Corporation. All rights reserved.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/Sync/IWorkspaceSynchronizer.cs
// Changed: DataverseClient → ISyncDataverseClient

using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using System.Collections.Immutable;
using System.Threading;

using Microsoft.CopilotStudio.McsCore;
namespace Microsoft.CopilotStudio.Sync;

public interface IWorkspaceSynchronizer
{
    /// <summary>
    /// Checks if synchronization information (mcs\conn.json) is available for the given workspace folder.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace</param>
    /// <returns>True if synchronization information (mcs\conn.json) is available; otherwise, false.</returns>
    bool IsSyncInfoAvailable(DirectoryPath workspaceFolder);

    /// <summary>
    /// Gets locally stored syncrhonization information for the workspace folder.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace</param>
    /// <returns><see cref="AgentSyncInfo"/> representing the connection details of the agent.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file that contains this information does not exist.</exception>
    Task<AgentSyncInfo> GetSyncInfoAsync(DirectoryPath workspaceFolder);

    /// <summary>
    /// Saves synchronization information for the workspace folder.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace</param>
    /// <param name="connectionDetails">The connection details to save</param>
    Task SaveSyncInfoAsync(DirectoryPath workspaceFolder, AgentSyncInfo connectionDetails);

    /// <summary>
    /// Gets the local changes in the workspace folder compared to the last pull.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace</param>
    /// <param name="workspaceDefinition">The current state of the workspace</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="agentId">The ID of the agent.</param>
    /// <param name="cancellationToken">Used to cancel the request</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, ISyncDataverseClient dataverseClient,
        Guid? agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the remote changes compared to the last pull.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace</param>
    /// <param name="operationContext">The operation context</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="agentId">The ID of the agent.</param>
    /// <param name="cancellationToken">Used to cancel the request</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetRemoteChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, ISyncDataverseClient dataverseClient,
        Guid? agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Performs an initial clone of an agent from the cloud service to a local workspace.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace</param>
    /// <param name="referenceTracker">track directory paths for other references</param>
    /// <param name="operationContext">Information about the authoring operation, such as the bot, the user, and organization</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service</param>
    /// <param name="agentId">The ID of the agent to clone</param>
    /// <param name="cancellationToken">Used to cancel the request</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task CloneChangesAsync(
        DirectoryPath workspaceFolder,
        ReferenceTracker referenceTracker,
        AuthoringOperationContextBase operationContext,
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Apply touchups for cross-workspace references.
    /// </summary>
    /// <param name="workspaceFolder">The source folder (containing a reference.mcs.yml) to touchup. </param>
    /// <param name="referenceTracker">Tracks directories of target workspaces</param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    Task ApplyTouchupsAsync(
        DirectoryPath workspaceFolder,
        ReferenceTracker referenceTracker,
        CancellationToken cancellation);

    /// <summary>
    /// Pulls incremental changes from cloud and writes them to disk.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace</param>
    /// <param name="operationContext">Information about the authoring operation, such as the bot, the user, and organization</param>
    /// <param name="localWorkspaceDefinition">The current state of the workspace to be updated</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="agentId">The ID of the agent.</param>
    /// <param name="cancellationToken">Used to cancel the request</param>
    /// <param name="downloadAllKnowledgeFiles">True/False to download or not all knowledge files.</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task<DefinitionBase> PullExistingChangesAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        DefinitionBase localWorkspaceDefinition,
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CancellationToken cancellationToken,
        bool downloadAllKnowledgeFiles = false);

    /// <summary>
    /// Pushes local changes to the cloud service and receives updated change information.
    /// </summary>
    /// <param name="workspaceFolder">The location of the root of the workspace</param>
    /// <param name="operationContext">Information about the authoring operation, such as the bot, the user, and organization</param>
    /// <param name="localWorkspaceDefinition">The local changes to be pushed to the cloud</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="agentId">The ID of the agent.</param>
    /// <param name="cloudFlowMetadata">Cloud flow metadata.</param>
    /// <param name="cancellationToken">Used to cancel the request</param>
    /// <param name="uploadAllKnowledgeFiles">True/False to upload or not all knowledge files.</param>
    /// <returns>Number of knowledge files uploaded to cloud.</returns>
    Task<int> PushChangesetAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        PvaComponentChangeSet localWorkspaceDefinition,
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CloudFlowMetadata? cloudFlowMetadata,
        CancellationToken cancellationToken,
        bool uploadAllKnowledgeFiles = false);

    /// <summary>
    /// Sync workspace to write bot definition, git ignore, change token files in .mcs.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="operationContext">Context.</param>
    /// <param name="changeToken">Change token.</param>
    /// <param name="updateWorkspaceDirectory">Whether to update workspace directory.</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="agentId">The ID of the agent.</param>
    /// <param name="cloudFlowMetadata">Cloud flow metadata to be written to workspace during sync.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Workspace sync result.</returns>
    Task<WorkspaceSyncInfo> SyncWorkspaceAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        string? changeToken,
        bool updateWorkspaceDirectory,
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CloudFlowMetadata? cloudFlowMetadata,
        CancellationToken cancellationToken);

    /// <summary>
    /// Upsert workflow for the specified agent.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="agentId">The ID of the agent.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>List of workflow responses.</returns>
    Task<(ImmutableArray<WorkflowResponse>, CloudFlowMetadata)> UpsertWorkflowForAgentAsync(
        DirectoryPath workspaceFolder,
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets workflows for the specified agent.
    /// </summary>
    /// <param name="workspaceFolder">Workspace folder.</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="agentId">The ID of the agent.</param>
    /// <param name="fileAccessor">The file accessor.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>CloudFlowMetadata</returns>
    Task<CloudFlowMetadata> GetWorkflowsAsync(
        DirectoryPath workspaceFolder,
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        IFileAccessor fileAccessor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Provisions connection references defined in portable connections.
    /// </summary>
    /// <param name="definition">The bot definition containing portable connections.</param>
    /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation</returns>
    Task ProvisionConnectionReferencesAsync(
        DefinitionBase definition,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the workspace definition from the specified folder.
    /// </summary>
    /// <param name="workspaceFolder">The workspace folder.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <param name="checkKnowledgeFiles">Whether to check knowledge files in the workspace.</param>
    /// <returns>The workspace definition.</returns>
    Task<DefinitionBase> ReadWorkspaceDefinitionAsync(DirectoryPath workspaceFolder, CancellationToken cancellationToken, bool checkKnowledgeFiles = false);

    /// <summary>
    /// Verifies a push by re-cloning the agent from the server to a temporary workspace
    /// and diffing against the expected (pushed) state. Returns per-entity-type results.
    /// This is a composed operation (clone + diff) per R9 push verification requirement.
    /// </summary>
    /// <param name="workspaceFolder">The workspace that was pushed (source of expected state).</param>
    /// <param name="operationContext">The operation context for the agent.</param>
    /// <param name="dataverseClient">Dataverse client for server communication.</param>
    /// <param name="agentId">The agent ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Verification result with per-entity-type acceptance status.</returns>
    Task<PushVerificationResult> VerifyPushAsync(
        DirectoryPath workspaceFolder,
        AuthoringOperationContextBase operationContext,
        ISyncDataverseClient dataverseClient,
        Guid? agentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Clones an agent and its component collections into separate workspace folders
    /// under <paramref name="rootFolder"/>. Each asset (agent or component collection)
    /// gets its own subfolder named after its display name. Cross-workspace references
    /// are resolved in a second pass via <see cref="ApplyTouchupsAsync"/>.
    /// </summary>
    /// <param name="rootFolder">Parent directory under which workspace subfolders are created.</param>
    /// <param name="syncInfo">Connection details for the agent (saved to each workspace).</param>
    /// <param name="assetsToClone">Which assets to clone (agent and/or component collection IDs).</param>
    /// <param name="agentInfo">Agent metadata including display names for folder naming.</param>
    /// <param name="operationContextProvider">Creates operation contexts for each asset.</param>
    /// <param name="dataverseClient">Dataverse client for server communication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of workspace directories created, agent folder first if present.</returns>
    Task<ImmutableArray<DirectoryPath>> CloneAllAssetsAsync(
        DirectoryPath rootFolder,
        AgentSyncInfo syncInfo,
        AssetsToClone assetsToClone,
        AgentInfo agentInfo,
        IOperationContextProvider operationContextProvider,
        ISyncDataverseClient dataverseClient,
        CancellationToken cancellationToken);
}
