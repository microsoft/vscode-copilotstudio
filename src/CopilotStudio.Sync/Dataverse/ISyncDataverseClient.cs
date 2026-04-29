// Copyright (C) Microsoft Corporation. All rights reserved.
// Renamed from IDataverseClient to ISyncDataverseClient to avoid collision with Platform's IDataverseClient.
// Ported from om/src/vscode/LanguageServers/PowerPlatformLS/Impl.PullAgent/Dataverse/IDataverseClient.cs

using System.Threading;
using Microsoft.Agents.ObjectModel;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.Dataverse;

public interface ISyncDataverseClient
{
    /// <summary>
    /// Sets the Dataverse URL for subsequent operations.
    /// Must be called before any other method.
    /// </summary>
#pragma warning disable CA1054 // URI parameter is used as string prefix for request URL construction
    void SetDataverseUrl(string dataverseUrl);
#pragma warning restore CA1054

    /// <summary>
    /// Create new agent by agent name and schema name.
    /// </summary>
    Task<AgentInfo> CreateNewAgentAsync(string displayName, string schemaName, CancellationToken cancellationToken);

    /// <summary>
    /// Get an agent with the given schemaName.
    /// </summary>
    Task<Guid> GetAgentIdBySchemaNameAsync(string schemaName, CancellationToken cancellationToken);

    /// <summary>
    /// Download all workflows for the specified agent.
    /// </summary>
    Task<WorkflowMetadata[]> DownloadAllWorkflowsForAgentAsync(AgentSyncInfo syncInfo, CancellationToken cancellationToken);

    /// <summary>
    /// Updates an existing workflow for the specified agent if exist, otherwise insert new workflow.
    /// </summary>
    Task<WorkflowResponse> UpdateWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new workflow for the specified agent.
    /// </summary>
    Task<WorkflowResponse> InsertWorkflowAsync(Guid? agentId, WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken);

    /// <summary>
    /// Checks if a connection reference exists.
    /// </summary>
    Task<bool> ConnectionReferenceExistsAsync(string connectionReferenceLogicalName, CancellationToken cancellationToken);

    /// <summary>
    /// Creates an unbound connection reference.
    /// </summary>
    Task CreateConnectionReferenceAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures connection reference exists (creates if missing).
    /// </summary>
    Task EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken);

    /// <summary>
    /// Get connection references by logical names.
    /// </summary>
    Task<ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken);

    /// <summary>
    /// Query Dataverse for solution versions needed by <see cref="SolutionInfo"/>.
    /// Fetches PowerVirtualAgents, msdyn_RelevanceSearch, and msft_AIPlatformExtensionsComponents.
    /// </summary>
    Task<SolutionInfo> GetSolutionVersionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Get agent metadata including component collection display names.
    /// Queries bots({agentId}) with $expand=bot_botcomponentcollection.
    /// </summary>
    Task<AgentInfo> GetAgentInfoAsync(Guid agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads a knowledge file to the local workspace.
    /// </summary>
    /// <param name="knowledgeFileFolder">The local workspace folder to download the knowledge file to.</param>
    /// <param name="botComponentId">The ID of the bot component to download the file from.</param>
    /// <param name="fileName">The name of the knowledge file to download.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task DownloadKnowledgeFileAsync(string knowledgeFileFolder, BotComponentId botComponentId, string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a knowledge file from the local workspace to the specified bot component.
    /// </summary>
    /// <param name="knowledgeFileFolder">The local workspace folder containing the knowledge file.</param>
    /// <param name="botComponentId">The ID of the bot component to upload the file to.</param>
    /// <param name="fileName">The name of the knowledge file to upload.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    Task UploadKnowledgeFileAsync(string knowledgeFileFolder, Guid botComponentId, string fileName, CancellationToken cancellationToken = default);
}
