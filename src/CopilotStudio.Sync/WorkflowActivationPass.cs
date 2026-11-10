// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.CopilotStudio.Sync;

/// <summary>One workflow as an activation pass left it.</summary>
public sealed record WorkflowActivationPassItem
{
    /// <summary>The workflow's identifier.</summary>
    public string WorkflowId { get; init; } = string.Empty;

    /// <summary>The workflow's display name, or its identifier when it has none.</summary>
    public string DisplayName { get; init; } = string.Empty;


    internal ImmutableArray<string> ConnectionReferenceLogicalNames { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>The state the service reported, or the workspace's recorded state when the service said nothing about it.</summary>
    public WorkflowState State { get; init; }

    /// <summary>Whether <see cref="State"/> is what the service reported in this pass rather than the workspace's own record.</summary>
    public bool IsObserved { get; init; }
}

/// <summary>What activating a workspace's drafts after a push achieved.</summary>
public sealed class WorkflowActivationPassResult
{
    /// <summary>Every workflow the workspace defines, ordered by display name, with the state the pass left it in.</summary>
    public ImmutableArray<WorkflowActivationPassItem> Workflows { get; init; } = ImmutableArray<WorkflowActivationPassItem>.Empty;

    /// <summary>The display names of the workflows the service reports as running, in ordinal order.</summary>
    public ImmutableArray<string> Activated { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>The explanation the service gave for refusing an activation, when it refused one.</summary>
    public string? RefusalMessage { get; init; }

    internal ImmutableArray<string> RefusedWorkflows { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>The connection references the pass left without a confirmed binding.</summary>
    public ImmutableArray<ConnectionBindingDecision> UnresolvedConnections { get; init; } = ImmutableArray<ConnectionBindingDecision>.Empty;

    /// <summary>Why the pass could not read, bind or activate, when it stopped early; the push it follows stands either way.</summary>
    public Exception? Failure { get; init; }

    /// <summary>How many workflows are not known to be running.</summary>
    public int NotRunningCount => Workflows.Count(workflow => workflow.State != WorkflowState.Activated);
}

/// <summary>Activates the workflows a push left as drafts once every connection they use is bound, first binding each unbound reference to a connected connection the principal owns.</summary>
public sealed class WorkflowActivationPass
{
    private readonly IConnectionManagementService _connections;

    private readonly IWorkflowActivationService _workflows;

    private readonly IFileAccessorFactory _fileAccessorFactory;

    /// <summary>Creates the pass.</summary>
    /// <param name="connections">Reads and binds the workspace's connection references.</param>
    /// <param name="workflows">Reads workflow eligibility and writes activation state.</param>
    /// <param name="fileAccessorFactory">Opens the workspace to tell a standalone workflow project from an agent.</param>
    public WorkflowActivationPass(IConnectionManagementService connections, IWorkflowActivationService workflows, IFileAccessorFactory fileAccessorFactory)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _workflows = workflows ?? throw new ArgumentNullException(nameof(workflows));
        _fileAccessorFactory = fileAccessorFactory ?? throw new ArgumentNullException(nameof(fileAccessorFactory));
    }

    /// <summary>Binds what can be bound without asking, activates every eligible draft and reports each workflow's state, returning any failure other than cancellation in the result so the push it follows stands.</summary>
    /// <param name="workspaceFolder">The workspace that was pushed.</param>
    /// <param name="definition">The workspace definition, or an empty one for a standalone workflow project.</param>
    /// <param name="dataverseClient">The client, already targeting the environment the push went to.</param>
    /// <param name="catalogClient">Lists the connections each connector has.</param>
    /// <param name="catalogContext">The Power Apps context the connections are listed in.</param>
    /// <param name="principal">The identity a connection has to belong to before it is bound without asking, or <see langword="null"/> to bind nothing.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    public async Task<WorkflowActivationPassResult> RunAsync(DirectoryPath workspaceFolder, DefinitionBase definition, ISyncDataverseClient dataverseClient, IConnectionCatalogClient catalogClient, PowerAppsContext catalogContext, string? principal, CancellationToken cancellationToken)
    {
        if (definition is null)
        {
            throw new ArgumentNullException(nameof(definition));
        }

        if (dataverseClient is null)
        {
            throw new ArgumentNullException(nameof(dataverseClient));
        }

        if (catalogClient is null)
        {
            throw new ArgumentNullException(nameof(catalogClient));
        }

        if (catalogContext is null)
        {
            throw new ArgumentNullException(nameof(catalogContext));
        }

        try
        {
            var views = await _connections.GetAgentConnectionViewsAsync(workspaceFolder, definition, dataverseClient, catalogClient, catalogContext, cancellationToken).ConfigureAwait(false);
            _connections.WriteConnectionsCache(workspaceFolder, views);

            var decisions = views.OrderBy(view => view.ConnectionReferenceLogicalName, StringComparer.Ordinal).Select(view => ConnectionBindingPlanner.Plan(view, principal)).ToArray();
            var bindings = decisions.Where(decision => decision.State == ConnectionBindingState.Bound).Select(decision => new ConnectionBindingRequest { ConnectionReferenceLogicalName = decision.View.ConnectionReferenceLogicalName, ConnectionId = decision.ConnectionId ?? throw new InvalidOperationException("A bound connection decision must name its connection.") }).ToImmutableArray();

            if (!bindings.IsEmpty)
            {
                views = await _connections.ApplyConnectionBindingsAsync(workspaceFolder, definition, dataverseClient, catalogClient, catalogContext, bindings, cancellationToken).ConfigureAwait(false);
            }

            var unresolved = decisions.Where(decision => !decision.IsResolved).ToImmutableArray();
            var workflows = _workflows.GetWorkflowStatusViews(workspaceFolder, views);

            if (workflows.Count == 0)
            {
                return new WorkflowActivationPassResult { UnresolvedConnections = unresolved };
            }

            var eligible = workflows.Where(workflow => workflow.CanEnable).ToArray();
            var requested = eligible.Where(workflow => Guid.TryParse(workflow.WorkflowId, out _)).ToArray();
            var activation = eligible.Length == 0 ? new WorkflowActivationResult { Succeeded = true } : await _workflows.SetWorkflowActivationsAsync(workspaceFolder, requested.Select(workflow => new WorkflowActivationRequest { WorkflowId = Guid.Parse(workflow.WorkflowId), Activate = true }).ToArray(), dataverseClient, cancellationToken).ConfigureAwait(false);
            var running = activation.Workflows.Where(workflow => workflow.State == WorkflowState.Activated).ToArray();

            return new WorkflowActivationPassResult
            {
                Workflows = Project(workflows, activation.Workflows, WorkflowWorkspace.IsStandaloneWorkspace(_fileAccessorFactory.Create(workspaceFolder))),
                Activated = running.Select(workflow => workflow.DisplayName).OrderBy(name => name, StringComparer.Ordinal).ToImmutableArray(),
                RefusalMessage = string.IsNullOrWhiteSpace(activation.Message) ? null : activation.Message,
                RefusedWorkflows = requested.Where(workflow => !running.Any(reported => string.Equals(reported.WorkflowId, workflow.WorkflowId, StringComparison.OrdinalIgnoreCase))).Select(DisplayNameOf).ToImmutableArray(),
                UnresolvedConnections = unresolved,
            };
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return new WorkflowActivationPassResult { Failure = failure };
        }
    }

    private static ImmutableArray<WorkflowActivationPassItem> Project(IReadOnlyList<WorkflowStatusView> workflows, ImmutableArray<WorkflowStatusView> reported, bool standalone)
    {
        var observedById = WorkflowResponseStates.ObservedById(reported);
        var observedByName = WorkflowResponseStates.Observed(reported);
        var items = workflows.Select(workflow => Recorded(workflow, standalone)).OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
        var identifyingNames = items.GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return items.Select(item => observedById.TryGetValue(item.WorkflowId, out var running) || (identifyingNames.Contains(item.DisplayName) && observedByName.TryGetValue(item.DisplayName, out running)) ? item with { State = running ? WorkflowState.Activated : WorkflowState.Draft, IsObserved = true } : item).ToImmutableArray();
    }

    private static WorkflowActivationPassItem Recorded(WorkflowStatusView workflow, bool standalone) => new()
    {
        WorkflowId = workflow.WorkflowId,
        DisplayName = DisplayNameOf(workflow),
        ConnectionReferenceLogicalNames = workflow.ConnectionReferenceLogicalNames,
        State = !standalone && workflow.State is WorkflowState.Activated or WorkflowState.Draft ? workflow.State : WorkflowState.Unknown,
    };

    private static string DisplayNameOf(WorkflowStatusView workflow) => string.IsNullOrWhiteSpace(workflow.DisplayName) ? workflow.WorkflowId : workflow.DisplayName;

    internal sealed class NoConnectionCatalog : IConnectionCatalogClient
    {
        public static readonly NoConnectionCatalog Instance = new();

        public static readonly PowerAppsContext Context = new();

        public Task<IReadOnlyList<ConnectionInstance>> ListConnectionsAsync(PowerAppsContext context, string connectorName, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectionInstance>>(Array.Empty<ConnectionInstance>());

        public Task<IReadOnlyList<ConnectorInfo>> ListConnectorsAsync(PowerAppsContext context, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectorInfo>>(Array.Empty<ConnectorInfo>());
    }
}
