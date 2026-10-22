// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync.Dataverse;

/// <summary>Reads and writes workflows outside an agent workspace.</summary>
public interface IStandaloneWorkflowDataverseClient : IConnectionReferenceOperations, IWorkflowStateOperations
{
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

    Task<bool> HasUnpublishedDraftAsync(Guid workflowId, CancellationToken cancellationToken);

    /// <summary>Lists the environment's unmanaged cloud flows without their definitions.</summary>
    /// <param name="nameFilter">Matches a substring of the display name, or null for all.</param>
    /// <param name="maximumCount">Caps the rows returned, or null for no cap.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The unmanaged cloud flows the environment holds.</returns>
    Task<SyncDataverseClient.WorkflowSummary[]> ListWorkflowsAsync(string? nameFilter, int? maximumCount, CancellationToken cancellationToken);

    /// <summary>Reads the names of the agents, or component collections, whose components use a workflow.</summary>
    /// <param name="workflowId">The workflow to look up.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The names in ordinal order, empty when nothing uses the workflow or the environment has no such row.</returns>
    Task<IReadOnlyList<string>> GetAgentsUsingWorkflowAsync(Guid workflowId, CancellationToken cancellationToken);

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
}
