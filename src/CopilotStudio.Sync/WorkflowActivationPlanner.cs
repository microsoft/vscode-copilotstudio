// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>Decides which workflows an environment is able to start.</summary>
public static class WorkflowActivationPlanner
{
    private const string ConnectionAuthorizationFailure = "ConnectionAuthorizationFailed";

    private static readonly string[] ConnectionFailureMarkers =
    {
        ConnectionAuthorizationFailure,
        "cannot be used to activate this flow",
        "this is not a valid connection",
        "Cannot find connection with name",
    };

    /// <summary>Whether a service failure says a connection the workflow binds cannot be used.</summary>
    /// <param name="failure">The message the service reported.</param>
    /// <returns><see langword="true"/> when the environment refused the workflow over a connection.</returns>
    public static bool IsConnectionAuthorizationFailure(string? failure) =>
        failure is { Length: > 0 } && ConnectionFailureMarkers.Any(marker => failure.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>Whether a failure, or anything it wraps, says a connection the workflow binds cannot be used.</summary>
    /// <param name="failure">The failure the environment raised.</param>
    /// <returns><see langword="true"/> when the environment refused the workflow over a connection.</returns>
    public static bool IsConnectionAuthorizationFailure(Exception? failure)
    {
        for (var current = failure; current is not null; current = current.InnerException)
        {
            if (IsConnectionAuthorizationFailure(current.Message))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a workflow binds any connection reference.</summary>
    public static bool HasConnectionReferences(SyncDataverseClient.WorkflowMetadata workflow)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        return workflow.ConnectionReferences.Any(logicalName => !string.IsNullOrWhiteSpace(logicalName));
    }

    /// <summary>The logical names of the connection references Dataverse reports bound to a connection.</summary>
    /// <param name="references">The connection references as Dataverse returned them.</param>
    /// <returns>The bound references, compared without regard to case.</returns>
    public static ISet<string> BoundReferences(IEnumerable<SyncDataverseClient.ConnectionReferenceInfo> references) => references is null ? throw new ArgumentNullException(nameof(references)) : new HashSet<string>(references.Where(reference => !string.IsNullOrWhiteSpace(reference.ConnectionId)).Select(reference => reference.ConnectionReferenceLogicalName), StringComparer.OrdinalIgnoreCase);

    /// <summary>The connection references a workflow uses that are not bound to a connection.</summary>
    /// <param name="connectionReferenceLogicalNames">The references the workflow uses, where a blank name is no reference.</param>
    /// <param name="boundReferences">The references bound to a connection.</param>
    /// <returns>Each unbound reference once, in the order the workflow uses them.</returns>
    public static IReadOnlyList<string> UnboundReferences(IEnumerable<string> connectionReferenceLogicalNames, ISet<string> boundReferences) => Unbound(connectionReferenceLogicalNames, boundReferences).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private static IEnumerable<string> Unbound(IEnumerable<string> connectionReferenceLogicalNames, ISet<string> boundReferences)
    {
        if (connectionReferenceLogicalNames is null)
        {
            throw new ArgumentNullException(nameof(connectionReferenceLogicalNames));
        }

        if (boundReferences is null)
        {
            throw new ArgumentNullException(nameof(boundReferences));
        }

        return connectionReferenceLogicalNames.Where(logicalName => !string.IsNullOrWhiteSpace(logicalName) && !boundReferences.Contains(logicalName));
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
    public static Task<IReadOnlyList<SyncDataverseClient.WorkflowMetadata>> DraftStandaloneUnboundActivationsAsync(IReadOnlyList<SyncDataverseClient.WorkflowMetadata> workflows, IConnectionReferenceOperations dataverseClient, CancellationToken cancellationToken) =>
        DraftUnboundActivationsAsync(workflows, ReadReferences(dataverseClient), cancellationToken);

    /// <summary>Drafts every workflow that wants to start but still has an unbound connection reference, from references already read.</summary>
    /// <param name="workflows">The workflows about to be written.</param>
    /// <param name="references">What the environment already reported for the references in play.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The workflows that were downgraded to drafts.</returns>
    public static Task<IReadOnlyList<SyncDataverseClient.WorkflowMetadata>> DraftUnboundActivationsAsync(IReadOnlyList<SyncDataverseClient.WorkflowMetadata> workflows, IReadOnlyCollection<SyncDataverseClient.ConnectionReferenceInfo> references, CancellationToken cancellationToken) =>
        references is null
            ? throw new ArgumentNullException(nameof(references))
            : DraftUnboundActivationsAsync(workflows, (_, _) => Task.FromResult(references.ToArray()), cancellationToken);

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
                ApplyState(workflow, activated: false);
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
            ApplyState(workflow, BindsOnlyBoundReferences(workflow, bound));
        }
    }

    /// <summary>Records the state a confirmed change leaves a workflow in.</summary>
    /// <param name="workflow">The workflow whose state is recorded.</param>
    /// <param name="activated">Whether the workflow is running rather than sitting as a draft.</param>
    public static void ApplyState(SyncDataverseClient.WorkflowMetadata workflow, bool activated)
    {
        if (workflow is null)
        {
            throw new ArgumentNullException(nameof(workflow));
        }

        workflow.StateCode = activated ? 1 : 0;
        workflow.StatusCode = activated ? 2 : 1;
    }

    private delegate Task<SyncDataverseClient.ConnectionReferenceInfo[]> ReferenceReader(IEnumerable<string> logicalNames, CancellationToken cancellationToken);

    private static ReferenceReader ReadReferences(ISyncDataverseClient dataverseClient) =>
        dataverseClient is null ? throw new ArgumentNullException(nameof(dataverseClient)) : dataverseClient.GetConnectionReferencesByLogicalNamesAsync;

    private static ReferenceReader ReadReferences(IConnectionReferenceOperations dataverseClient) =>
        dataverseClient is null ? throw new ArgumentNullException(nameof(dataverseClient)) : dataverseClient.GetConnectionReferencesByLogicalNamesAsync;

    /// <summary>Describes a workflow that was saved as a draft because a connection it binds is unusable.</summary>
    /// <param name="workflowName">The workflow that was drafted.</param>
    /// <returns>A message naming what to bind before enabling it.</returns>
    public static string DescribeDraftedForConnections(string workflowName) =>
        $"Workflow '{workflowName}' was saved as a draft because one of its connection references has no connection you can use. Bind a connection, then enable it.";

    /// <summary>Describes a workflow held back as a draft until its connection references are set up here.</summary>
    /// <param name="workflowName">The workflow that was drafted.</param>
    /// <returns>A message naming what to finish before enabling it.</returns>
    public static string DescribeDraftedForConnectionSetup(string workflowName) =>
        $"Workflow '{workflowName}' was saved as a draft because it binds connection references that have to be set up in this environment first. Set them up, then enable it.";

    /// <summary>Describes a state change the environment did not apply, or an empty string when it did.</summary>
    /// <param name="outcome">What the environment established.</param>
    /// <param name="workflowName">The workflow the change was requested for.</param>
    /// <param name="activate">Whether the change asked the workflow to run.</param>
    /// <returns>A message naming what the workflow is left doing, or an empty string.</returns>
    public static string DescribeUnapplied(WorkflowStateOutcome outcome, string workflowName, bool activate)
    {
        if (outcome is null)
        {
            throw new ArgumentNullException(nameof(outcome));
        }

        if (outcome.Changed)
        {
            return string.Empty;
        }

        if (activate && IsConnectionAuthorizationFailure(outcome.Failure))
        {
            return DescribeDraftedForConnections(workflowName);
        }

        var requested = activate ? "enabled" : "turned off";

        return outcome.IsUnknown
            ? $"Workflow '{workflowName}' was saved, but the environment did not confirm whether it was {requested} ({outcome.Failure}). Check its state before relying on it."
            : $"Workflow '{workflowName}' was saved, but the environment refused to have it {requested} ({outcome.Failure}).";
    }

    private static async Task<ISet<string>> BoundLogicalNamesAsync(IReadOnlyList<SyncDataverseClient.WorkflowMetadata> workflows, ReferenceReader readReferences, CancellationToken cancellationToken)
    {
        var logicalNames = workflows.SelectMany(DeclaredReferences).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (logicalNames.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return BoundReferences(await readReferences(logicalNames, cancellationToken).ConfigureAwait(false));
    }

    private static bool BindsOnlyBoundReferences(SyncDataverseClient.WorkflowMetadata workflow, ISet<string> bound) => !Unbound(workflow.ConnectionReferences, bound).Any();

    private static IEnumerable<string> DeclaredReferences(SyncDataverseClient.WorkflowMetadata workflow) =>
        workflow.ConnectionReferences.Where(logicalName => !string.IsNullOrWhiteSpace(logicalName));

    /// <summary>Stops a workflow, reporting rather than throwing when the environment refuses.</summary>
    /// <param name="workflowId">The workflow to stop.</param>
    /// <param name="dataverseClient">Writes the state change.</param>
    /// <param name="cancellationToken">Cancels the state change.</param>
    /// <returns>What the environment established about the workflow's state.</returns>
    public static async Task<WorkflowStateOutcome> TryStopAsync(Guid workflowId, IWorkflowStateOperations dataverseClient, CancellationToken cancellationToken)
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
    public static async Task<WorkflowStateOutcome> TryStartAsync(SyncDataverseClient.WorkflowMetadata workflow, IWorkflowStateOperations dataverseClient, CancellationToken cancellationToken)
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
            return Classify(failure);
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
