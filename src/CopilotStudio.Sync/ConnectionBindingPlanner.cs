// Copyright (C) Microsoft Corporation. All rights reserved.

namespace Microsoft.CopilotStudio.Sync;

/// <summary>What the planner decided for one connection reference.</summary>
public enum ConnectionBindingState
{
    /// <summary>The reference is bound to a connection the catalog confirmed.</summary>
    AlreadyBound = 0,

    /// <summary>A connected connection the principal owns was chosen to bind.</summary>
    Bound = 1,

    /// <summary>No connected connection exists for the reference.</summary>
    NoCandidate = 2,

    /// <summary>A connected connection could satisfy the reference, but none is provably the principal's own.</summary>
    Unbound = 3,

    /// <summary>A component uses the reference but the workspace does not declare it.</summary>
    Undeclared = 4,
}

/// <summary>What the planner decided for one connection reference.</summary>
/// <param name="View">The reference the decision is about.</param>
/// <param name="State">The decided state.</param>
/// <param name="ConnectionId">The connection that is bound or chosen to bind, when there is one.</param>
/// <param name="Candidates">The connected candidates that were considered, in connection id order.</param>
public sealed record ConnectionBindingDecision(AgentConnectionView View, ConnectionBindingState State, string? ConnectionId, IReadOnlyList<ConnectionInstance> Candidates)
{
    internal bool IsResolved => State is ConnectionBindingState.Bound or ConnectionBindingState.AlreadyBound;
}

/// <summary>Chooses, without any I/O, the connection each reference may be bound to without asking: only a connected connection the principal provably owns, the first by connection id.</summary>
public static class ConnectionBindingPlanner
{
    private const string ConnectedStatus = "Connected";

    /// <summary>Reports what is true of one reference without proposing a binding.</summary>
    /// <param name="view">The reference to read.</param>
    public static ConnectionBindingDecision Observe(AgentConnectionView view)
    {
        if (view is null)
        {
            throw new ArgumentNullException(nameof(view));
        }

        var connected = view.Candidates.Where(candidate => string.Equals(candidate.Status, ConnectedStatus, StringComparison.OrdinalIgnoreCase)).OrderBy(candidate => candidate.Name, StringComparer.Ordinal).ToArray();
        var boundConnectionId = view.BoundConnectionExists && !view.CatalogUnavailable && !string.IsNullOrWhiteSpace(view.BoundConnectionId) ? view.BoundConnectionId : null;

        if (!view.IsDeclared)
        {
            return new ConnectionBindingDecision(view, connected.Length == 0 || boundConnectionId is not null ? ConnectionBindingState.Undeclared : ConnectionBindingState.Unbound, null, connected);
        }

        if (boundConnectionId is not null)
        {
            return new ConnectionBindingDecision(view, ConnectionBindingState.AlreadyBound, boundConnectionId, Array.Empty<ConnectionInstance>());
        }

        return connected.Length == 0 ? new ConnectionBindingDecision(view, ConnectionBindingState.NoCandidate, null, Array.Empty<ConnectionInstance>()) : new ConnectionBindingDecision(view, ConnectionBindingState.Unbound, null, connected);
    }

    /// <summary>Decides what a run that may write should do with one reference, binding an unbound one only to a connected connection the principal owns.</summary>
    /// <param name="view">The reference to plan for, with its candidates.</param>
    /// <param name="principal">The identity a connection has to belong to before it is bound without asking, or <see langword="null"/> to bind nothing.</param>
    public static ConnectionBindingDecision Plan(AgentConnectionView view, string? principal)
    {
        var observed = Observe(view);

        if (observed.State != ConnectionBindingState.Unbound || string.IsNullOrWhiteSpace(principal))
        {
            return observed;
        }

        var owned = observed.Candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate.Owner) && candidate.Owner.Equals(principal, StringComparison.OrdinalIgnoreCase));

        return owned is null ? observed : observed with { State = ConnectionBindingState.Bound, ConnectionId = owned.Name };
    }
}
