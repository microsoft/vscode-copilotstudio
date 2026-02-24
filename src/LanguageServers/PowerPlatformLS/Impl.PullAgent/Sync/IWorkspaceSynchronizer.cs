namespace Microsoft.PowerPlatformLS.Impl.PullAgent
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.PowerPlatformLS.Contracts.Internal.Common;
    using Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse;
    using System.Collections.Immutable;

    internal interface IWorkspaceSynchronizer
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
        /// <param name="cancellationToken">Used to cancel the request</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetLocalChangesAsync(DirectoryPath workspaceFolder, DefinitionBase workspaceDefinition, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the remote changes compared to the last pull.
        /// </summary>
        /// <param name="workspaceFolder">The location of the root of the workspace</param>
        /// <param name="operationContext">The operation context</param>
        /// <param name="cancellationToken">Used to cancel the request</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task<(PvaComponentChangeSet, ImmutableArray<Change>)> GetRemoteChangesAsync(DirectoryPath workspaceFolder, AuthoringOperationContextBase operationContext, CancellationToken cancellationToken);

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
            DataverseClient dataverseClient,
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
        /// <returns>A task representing the asynchronous operation</returns>
        Task<DefinitionBase> PullExistingChangesAsync(
            DirectoryPath workspaceFolder,
            AuthoringOperationContextBase operationContext,
            DefinitionBase localWorkspaceDefinition,
            DataverseClient dataverseClient,
            Guid? agentId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Pushes local changes to the cloud service and receives updated change information.
        /// </summary>
        /// <param name="workspaceFolder">The location of the root of the workspace</param>
        /// <param name="operationContext">Information about the authoring operation, such as the bot, the user, and organization</param>
        /// <param name="localWorkspaceDefinition">The local changes to be pushed to the cloud</param>
        /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
        /// <param name="agentId">The ID of the agent.</param>
        /// <param name="cancellationToken">Used to cancel the request</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task PushChangesetAsync(
            DirectoryPath workspaceFolder,
            AuthoringOperationContextBase operationContext,
            PvaComponentChangeSet localWorkspaceDefinition,
            DataverseClient dataverseClient,
            Guid? agentId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Sync workspace to write bot definition, git ignore, change token files in .mcs.
        /// </summary>
        /// <param name="workspaceFolder">Workspace folder.</param>
        /// <param name="operationContext">Context.</param>
        /// <param name="changeToken">Change token.</param>
        /// <param name="updateWorkspaceDirectory">Whether to update workspace directory.</param>
        /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
        /// <param name="agentId">The ID of the agent.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>Workspace sync result.</returns>
        Task<WorkspaceSyncInfo> SyncWorkspaceAsync(
            DirectoryPath workspaceFolder,
            AuthoringOperationContextBase operationContext,
            string? changeToken,
            bool updateWorkspaceDirectory,
            DataverseClient dataverseClient,
            Guid? agentId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Upsert workflow for the specified agent.
        /// </summary>
        /// <param name="workspaceFolder">Workspace folder.</param>
        /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
        /// <param name="agentId">The ID of the agent.</param>
        /// <param name="isInsert">True/False for insert/update workflow.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>A task representing the asynchronous operation</returns>
        Task UpsertWorkflowForAgentAsync(
            DirectoryPath workspaceFolder,
            DataverseClient dataverseClient,
            Guid? agentId,
            bool isInsert,
            CancellationToken cancellationToken);

        /// <summary>
        /// Gets workflows for the specified agent.
        /// </summary>
        /// <param name="workspaceFolder">Workspace folder.</param>
        /// <param name="dataverseClient">The dataverse client to use for communication with the dataverse service.</param>
        /// <param name="agentId">The ID of the agent.</param>
        /// <param name="fileAccessor">The file accessor.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>list of CloudFlowDefinition</returns>
        Task<ImmutableArray<CloudFlowDefinition>> GetWorkflowsAsync(
            DirectoryPath workspaceFolder,
            DataverseClient dataverseClient,
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
            DataverseClient dataverseClient,
            CancellationToken cancellationToken);
    }
}
