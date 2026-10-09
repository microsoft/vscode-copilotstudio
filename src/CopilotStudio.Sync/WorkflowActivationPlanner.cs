// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Decides which workflows an environment is able to start.</summary>
public static class WorkflowActivationPlanner
{
    private const string ConnectionAuthorizationFailure = "ConnectionAuthorizationFailed";

    /// <summary>Whether a service failure says a connection the workflow binds cannot be used.</summary>
    /// <param name="failure">The message the service reported.</param>
    /// <returns><see langword="true"/> when the environment refused the workflow over a connection.</returns>
    public static bool IsConnectionAuthorizationFailure(string? failure) =>
        failure?.IndexOf(ConnectionAuthorizationFailure, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Whether a workflow binds any connection reference.</summary>
    public static bool HasConnectionReferences(SyncDataverseClient.WorkflowMetadata workflow)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        return workflow.ConnectionReferences.Any(logicalName => !string.IsNullOrWhiteSpace(logicalName));
    }

    /// <summary>Drafts every workflow that wants to start but still has an unbound connection reference.</summary>
    /// <param name="workflows">The workflows about to be written.</param>
    /// <param name="dataverseClient">Reads which references carry a connection.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The workflows that were downgraded to drafts.</returns>
    public static Task<IReadOnlyList<SyncDataverseClient.WorkflowMetadata>> DraftUnboundActivationsAsync(IReadOnlyList<SyncDataverseClient.WorkflowMetadata> workflows, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken) =>
        DraftUnboundActivationsAsync(workflows, ReadReferences(dataverseClient), cancellationToken);

    /// <summary>Drafts every workflow that wants to start but still has an unbound connection reference.</summary>
    /// <param name="workflows">The workflows about to be written.</param>
    /// <param name="dataverseClient">Reads which references carry a connection.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The workflows that were downgraded to drafts.</returns>
    public static Task<IReadOnlyList<SyncDataverseClient.WorkflowMetadata>> DraftUnboundActivationsAsync(IReadOnlyList<SyncDataverseClient.WorkflowMetadata> workflows, IStandaloneWorkflowDataverseClient dataverseClient, CancellationToken cancellationToken) =>
        DraftUnboundActivationsAsync(workflows, ReadReferences(dataverseClient), cancellationToken);

    private static async Task<IReadOnlyList<SyncDataverseClient.WorkflowMetadata>> DraftUnboundActivationsAsync(IReadOnlyList<SyncDataverseClient.WorkflowMetadata> workflows, ReferenceReader readReferences, CancellationToken cancellationToken)
    {
        if (workflows is null)
        {
            throw new ArgumentNullException(nameof(workflows));
        }

        var activating = workflows.Where(workflow => workflow.StateCode == 1 && HasConnectionReferences(workflow)).ToList();

        if (activating.Count == 0)
        {
            return Array.Empty<SyncDataverseClient.WorkflowMetadata>();
        }

        var bound = await BoundLogicalNamesAsync(activating, readReferences, cancellationToken).ConfigureAwait(false);

        var downgraded = new List<SyncDataverseClient.WorkflowMetadata>();

        foreach (var workflow in activating)
        {
            if (!BindsOnlyBoundReferences(workflow, bound))
            {
                workflow.StateCode = 0;
                workflow.StatusCode = 1;
                downgraded.Add(workflow);
            }
        }

        return downgraded;
    }

    /// <summary>Starts every workflow whose connection references all carry a connection, drafting the rest.</summary>
    /// <param name="workflows">The workflows about to be written.</param>
    /// <param name="dataverseClient">Reads which references carry a connection.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    public static async Task ActivateWhenBoundAsync(IReadOnlyList<SyncDataverseClient.WorkflowMetadata> workflows, ISyncDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (workflows is null)
        {
            throw new ArgumentNullException(nameof(workflows));
        }

        var bound = await BoundLogicalNamesAsync(workflows, ReadReferences(dataverseClient), cancellationToken).ConfigureAwait(false);

        foreach (var workflow in workflows)
        {
            var runnable = BindsOnlyBoundReferences(workflow, bound);

            workflow.StateCode = runnable ? 1 : 0;
            workflow.StatusCode = runnable ? 2 : 1;
        }
    }

    private delegate Task<SyncDataverseClient.ConnectionReferenceInfo[]> ReferenceReader(IEnumerable<string> logicalNames, CancellationToken cancellationToken);

    private static ReferenceReader ReadReferences(ISyncDataverseClient dataverseClient) =>
        dataverseClient is null ? throw new ArgumentNullException(nameof(dataverseClient)) : dataverseClient.GetConnectionReferencesByLogicalNamesAsync;

    private static ReferenceReader ReadReferences(IStandaloneWorkflowDataverseClient dataverseClient) =>
        dataverseClient is null ? throw new ArgumentNullException(nameof(dataverseClient)) : dataverseClient.GetConnectionReferencesByLogicalNamesAsync;

    private static async Task<HashSet<string>> BoundLogicalNamesAsync(IReadOnlyList<SyncDataverseClient.WorkflowMetadata> workflows, ReferenceReader readReferences, CancellationToken cancellationToken)
    {
        var logicalNames = workflows.SelectMany(DeclaredReferences).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (logicalNames.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var references = await readReferences(logicalNames, cancellationToken).ConfigureAwait(false);

        return new HashSet<string>(
            references.Where(reference => !string.IsNullOrWhiteSpace(reference.ConnectionId)).Select(reference => reference.ConnectionReferenceLogicalName),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool BindsOnlyBoundReferences(SyncDataverseClient.WorkflowMetadata workflow, HashSet<string> bound) => DeclaredReferences(workflow).All(bound.Contains);

    private static IEnumerable<string> DeclaredReferences(SyncDataverseClient.WorkflowMetadata workflow) =>
        workflow.ConnectionReferences.Where(logicalName => !string.IsNullOrWhiteSpace(logicalName));

    /// <summary>Stops a workflow, reporting rather than throwing when the environment refuses.</summary>
    /// <param name="workflowId">The workflow to stop.</param>
    /// <param name="dataverseClient">Writes the state change.</param>
    /// <param name="cancellationToken">Cancels the state change.</param>
    /// <returns>What the environment established about the workflow's state.</returns>
    public static async Task<WorkflowStateOutcome> TryStopAsync(Guid workflowId, IStandaloneWorkflowDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (dataverseClient is null)
        {
            throw new ArgumentNullException(nameof(dataverseClient));
        }

        try
        {
            await dataverseClient.SetWorkflowStateAsync(workflowId, activate: false, cancellationToken).ConfigureAwait(false);
        }
        catch (DataverseRequestException failure)
        {
            return Classify(failure);
        }

        return WorkflowStateOutcome.Applied;
    }

    /// <summary>Starts a workflow, reporting rather than throwing when the environment refuses.</summary>
    /// <param name="workflow">The workflow to start, whose state is updated when the outcome is known.</param>
    /// <param name="dataverseClient">Writes the state change.</param>
    /// <param name="cancellationToken">Cancels the state change.</param>
    /// <returns>What the environment established about the workflow's state.</returns>
    public static async Task<WorkflowStateOutcome> TryStartAsync(SyncDataverseClient.WorkflowMetadata workflow, IStandaloneWorkflowDataverseClient dataverseClient, CancellationToken cancellationToken)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        if (dataverseClient is null)
        {
            throw new ArgumentNullException(nameof(dataverseClient));
        }

        try
        {
            await dataverseClient.SetWorkflowStateAsync(workflow.WorkflowId, activate: true, cancellationToken).ConfigureAwait(false);
        }
        catch (DataverseRequestException failure)
        {
            var outcome = Classify(failure);

            if (outcome.Refused)
            {
                workflow.StateCode = 0;
                workflow.StatusCode = 1;
            }

            return outcome;
        }

        workflow.StateCode = 1;
        workflow.StatusCode = 2;

        return WorkflowStateOutcome.Applied;
    }

    /// <summary>
    /// Whether a status says the environment decided, rather than that the request never arrived at
    /// a decision. A gateway or server failure leaves the workflow's state unknown, so a caller that
    /// treated it as a refusal would report a state the environment never confirmed.
    /// </summary>
    private static WorkflowStateOutcome Classify(DataverseRequestException failure) =>
        (int)failure.StatusCode is >= 400 and < 500 && failure.StatusCode != System.Net.HttpStatusCode.RequestTimeout
            ? WorkflowStateOutcome.Refusal(failure.Message)
            : WorkflowStateOutcome.Unknown(failure.Message);
}
