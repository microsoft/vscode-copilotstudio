// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Reads and writes workflows that belong to no agent.</summary>
public interface IStandaloneWorkflowDataverseClient
{
    /// <summary>Sets the environment every later call targets.</summary>
    /// <param name="dataverseUrl">The environment's Dataverse endpoint.</param>
#pragma warning disable CA1054 // URI parameter is used as string prefix for request URL construction
    void SetDataverseUrl(string dataverseUrl);
#pragma warning restore CA1054

    /// <summary>Inserts a workflow the environment owns without an agent of its own.</summary>
    /// <param name="workflowMetadata">The workflow to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome, carrying a message when the write failed.</returns>
    Task<WorkflowResponse> InsertWorkflowAsync(SyncDataverseClient.WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken);

    /// <summary>Reads one workflow by id, including its definition.</summary>
    /// <param name="workflowId">The workflow to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The workflow, or <see langword="null"/> when the environment has no such row.</returns>
    Task<SyncDataverseClient.WorkflowMetadata?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Lists cloud flows in the environment without their definitions.</summary>
    /// <param name="nameFilter">Matches a substring of the display name, or null for all.</param>
    /// <param name="maximumCount">Caps the rows returned, or null for no cap.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The flows the environment holds.</returns>
    Task<SyncDataverseClient.WorkflowSummary[]> ListWorkflowsAsync(string? nameFilter, int? maximumCount, CancellationToken cancellationToken);

    /// <summary>Stops a running workflow and then deletes it.</summary>
    /// <param name="workflowId">The workflow to delete.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see langword="false"/> when the environment had no such row.</returns>
    Task<bool> DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Writes only a workflow's definition and naming, leaving every other column as the environment has it.</summary>
    /// <param name="workflowMetadata">The workflow whose definition is written.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The outcome, carrying a message when the write failed.</returns>
    Task<WorkflowResponse> UpdateWorkflowDefinitionAsync(SyncDataverseClient.WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken);

    /// <summary>Starts or stops a workflow.</summary>
    /// <param name="workflowId">The workflow whose state changes.</param>
    /// <param name="activate">Whether the workflow should run.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SetWorkflowStateAsync(Guid workflowId, bool activate, CancellationToken cancellationToken);

    /// <summary>Creates a connection reference when the environment does not already hold one.</summary>
    /// <param name="connectionReferenceLogicalName">The connection reference to create.</param>
    /// <param name="connectorId">The connector the reference targets.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <param name="customConnectorRowId">The custom connector row the reference targets, when it is not a first-party connector.</param>
    Task EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken, Guid? customConnectorRowId = null);

    /// <summary>Reads connection references by logical name, skipping any the environment does not hold.</summary>
    /// <param name="logicalNames">The connection references to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The references the environment holds, with the connection each is bound to.</returns>
    Task<SyncDataverseClient.ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken);
}
