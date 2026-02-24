namespace Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse
{
    using System.Threading.Tasks;
    using static Microsoft.PowerPlatformLS.Impl.PullAgent.Dataverse.DataverseClient;

    internal interface IDataverseClient
    {
        /// <summary>
        /// Create new agent by agent name and schema name.
        /// </summary>
        /// <param name="displayName">Display name of the new agent.</param>
        /// <param name="schemaName">Schema name for the new agent.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>The created agent information.</returns>
        Task<AgentInfo> CreateNewAgentAsync(string displayName, string schemaName, CancellationToken cancellationToken);

        /// <summary>
        /// Get an agent with the given schemaName.
        /// </summary>
        /// <param name="schemaName">Schema name to search for.</param>
        /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
        /// <returns>Agent ID if found, otherwise Guid.Empty.</returns>
        Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken);

        /// <summary>
        /// Download all workflows for the specified agent.
        /// </summary>
        /// <param name="agentId"></param>
        /// <param name="cancellationToken"></param>
        /// <returns>The workflow metadata.</returns>
        Task<WorkflowMetadata[]> DownloadAllWorkflowsForAgentAsync(Guid? agentId, CancellationToken cancellationToken);

        /// <summary>
        /// Updates an existing workflow for the specified agent if exist, otherwise insert new workflow.
        /// </summary>
        /// <param name="agentId">The ID of the agent.</param>
        /// <param name="workflowMetadata">The workflow metadata to update.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        Task UpdateWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken);

        /// <summary>
        /// Inserts a new workflow for the specified agent.
        /// <param name="agentId">The ID of the agent.</param>
        /// <param name="workflowMetadata">The workflow metadata to update.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// </summary>
        Task InsertWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken);

        /// <summary>
        /// Checks if a connection reference exists.
        /// GET /api/data/v9.2/connectionreferences?$filter=connectionreferencelogicalname eq '{name}'
        /// </summary>
        Task<bool> ConnectionReferenceExistsAsync(string connectionReferenceLogicalName, CancellationToken cancellationToken);

        /// <summary>
        /// Creates an unbound connection reference.
        /// POST /api/data/v9.2/connectionreferences
        /// Body: { "connectionreferencelogicalname": "...", "connectorid": "..." }
        /// </summary>
        Task CreateConnectionReferenceAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken);

        /// <summary>
        /// Ensures connection reference exists (creates if missing).
        /// </summary>
        Task EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken);
    }
}
