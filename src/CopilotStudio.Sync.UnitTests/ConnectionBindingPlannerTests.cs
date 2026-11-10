// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal static class ConnectionViewFixtures
{
    public const string Principal = "op@contoso.com";

    public const string TeamsConnectorId = "/providers/Microsoft.PowerApps/apis/shared_teams";

    public static AgentConnectionView Reference(string logicalName, string? boundConnectionId = null, params ConnectionInstance[] candidates) => new()
    {
        ConnectionReferenceLogicalName = logicalName,
        ConnectorId = TeamsConnectorId,
        ConnectorName = "shared_teams",
        BoundConnectionId = boundConnectionId ?? string.Empty,
        BoundConnectionExists = boundConnectionId is not null,
        Candidates = candidates.ToImmutableArray(),
    };

    public static ConnectionInstance Candidate(string name, string status = "Connected", string owner = Principal) => new() { Name = name, DisplayName = $"{name}@contoso.com", Status = status, Owner = owner };
}

public class ConnectionBindingPlannerTests
{
    private static ConnectionBindingDecision Plan(AgentConnectionView view) => ConnectionBindingPlanner.Plan(view, ConnectionViewFixtures.Principal);

    [Fact]
    public void Plan_WithExactlyOneConnectedCandidate_Binds()
    {
        var decision = Plan(ConnectionViewFixtures.Reference("cr_office", null, ConnectionViewFixtures.Candidate("conn-1")));

        Assert.Equal(ConnectionBindingState.Bound, decision.State);
        Assert.Equal("conn-1", decision.ConnectionId);
        Assert.True(decision.IsResolved);
    }

    [Fact]
    public void Plan_WithTwoConnectedCandidatesThePrincipalOwns_BindsTheFirst()
    {
        var decision = Plan(ConnectionViewFixtures.Reference("cr_sql", null, ConnectionViewFixtures.Candidate("conn-1"), ConnectionViewFixtures.Candidate("conn-2")));

        Assert.Equal(ConnectionBindingState.Bound, decision.State);
        Assert.Equal("conn-1", decision.ConnectionId);
        Assert.Equal(2, decision.Candidates.Count);
    }

    [Fact]
    public void Plan_WithSeveralCandidatesThePrincipalOwns_BindsTheFirstById()
    {
        var decision = Plan(ConnectionViewFixtures.Reference("cr_teams", null, ConnectionViewFixtures.Candidate("conn-b"), ConnectionViewFixtures.Candidate("conn-a", owner: ConnectionViewFixtures.Principal.ToUpperInvariant())));

        Assert.Equal(ConnectionBindingState.Bound, decision.State);
        Assert.Equal("conn-a", decision.ConnectionId);
    }

    [Fact]
    public void Plan_WithCandidatesOwnedByOthers_BindsNothing()
    {
        var view = ConnectionViewFixtures.Reference(
            "cr_teams",
            null,
            ConnectionViewFixtures.Candidate("conn-a", owner: "svc-expense@contoso.com"),
            ConnectionViewFixtures.Candidate("conn-b", owner: "alice@contoso.com"),
            ConnectionViewFixtures.Candidate("conn-c", owner: "alice@contoso.com"));

        var decision = Plan(view);

        Assert.Equal(ConnectionBindingState.Unbound, decision.State);
        Assert.Null(decision.ConnectionId);
        Assert.Equal(3, decision.Candidates.Count);
        Assert.False(decision.IsResolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Plan_WithACandidateWhoseOwnerIsUnknown_BindsNothing(string owner)
    {
        Assert.Equal(ConnectionBindingState.Unbound, Plan(ConnectionViewFixtures.Reference("cr_teams", null, ConnectionViewFixtures.Candidate("conn-a", owner: owner))).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Plan_WithNobodySignedIn_BindsNothing(string? principal)
    {
        var decision = ConnectionBindingPlanner.Plan(ConnectionViewFixtures.Reference("cr_teams", null, ConnectionViewFixtures.Candidate("conn-a")), principal);

        Assert.Equal(ConnectionBindingState.Unbound, decision.State);
        Assert.Null(decision.ConnectionId);
    }

    [Fact]
    public void Plan_WithSeveralCandidatesWhoseOwnerIsUnknown_NeverPoolsThemIntoAMajority()
    {
        var view = ConnectionViewFixtures.Reference(
            "cr_teams",
            null,
            ConnectionViewFixtures.Candidate("conn-c", owner: string.Empty),
            ConnectionViewFixtures.Candidate("conn-b", owner: string.Empty),
            ConnectionViewFixtures.Candidate("conn-d"));

        Assert.Equal("conn-d", Plan(view).ConnectionId);
    }

    [Fact]
    public void Plan_WithADisconnectedCandidateOfItsOwn_StillBindsTheConnectedOne()
    {
        var decision = Plan(ConnectionViewFixtures.Reference("cr_teams", null, ConnectionViewFixtures.Candidate("conn-a", "Error"), ConnectionViewFixtures.Candidate("conn-b")));

        Assert.Equal(ConnectionBindingState.Bound, decision.State);
        Assert.Equal("conn-b", decision.ConnectionId);
        Assert.Equal("conn-b", Assert.Single(decision.Candidates).Name);
    }

    [Fact]
    public void Plan_WithNoCandidates_HasNoCandidate()
    {
        var decision = Plan(ConnectionViewFixtures.Reference("cr_http"));

        Assert.Equal(ConnectionBindingState.NoCandidate, decision.State);
        Assert.Empty(decision.Candidates);
        Assert.False(decision.IsResolved);
    }

    [Fact]
    public void Plan_IgnoresCandidatesThatAreNotConnected()
    {
        Assert.Equal(ConnectionBindingState.NoCandidate, Plan(ConnectionViewFixtures.Reference("cr_http", null, ConnectionViewFixtures.Candidate("conn-1", "Disconnected"))).State);
    }

    [Fact]
    public void Plan_AnAlreadyBoundReference_KeepsItsConnection()
    {
        var decision = Plan(ConnectionViewFixtures.Reference("cr_office", "conn-existing", ConnectionViewFixtures.Candidate("conn-other")));

        Assert.Equal(ConnectionBindingState.AlreadyBound, decision.State);
        Assert.Equal("conn-existing", decision.ConnectionId);
        Assert.Empty(decision.Candidates);
        Assert.True(decision.IsResolved);
    }

    [Fact]
    public void Plan_AnUndeclaredReferenceWithAConnection_IsBoundWhichDeclaresIt()
    {
        var decision = Plan(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", ConnectorName = "shared_teams", Candidates = ImmutableArray.Create(ConnectionViewFixtures.Candidate("conn-1")), IsDeclared = false });

        Assert.Equal(ConnectionBindingState.Bound, decision.State);
        Assert.Equal("conn-1", decision.ConnectionId);
    }

    [Fact]
    public void Plan_AnUndeclaredReferenceWithNoConnection_StaysUndeclared()
    {
        var decision = Plan(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", ConnectorName = "shared_teams", IsDeclared = false });

        Assert.Equal(ConnectionBindingState.Undeclared, decision.State);
        Assert.Null(decision.ConnectionId);
        Assert.False(decision.IsResolved);
    }

    [Fact]
    public void Plan_AnUndeclaredReferenceThatIsAlreadyBound_IsStillUndeclared()
    {
        Assert.Equal(ConnectionBindingState.Undeclared, Plan(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", BoundConnectionId = "conn-existing", BoundConnectionExists = true, Candidates = ImmutableArray.Create(ConnectionViewFixtures.Candidate("conn-existing")), IsDeclared = false }).State);
    }

    [Fact]
    public void Observe_NeverProposesAConnection()
    {
        var observed = ConnectionBindingPlanner.Observe(ConnectionViewFixtures.Reference("cr_sql", null, ConnectionViewFixtures.Candidate("conn-1")));

        Assert.Equal(ConnectionBindingState.Unbound, observed.State);
        Assert.Null(observed.ConnectionId);
        Assert.False(observed.IsResolved);
        Assert.Single(observed.Candidates);
    }

    [Fact]
    public void Plan_ProposesWhereObserveOnlyReports()
    {
        var view = ConnectionViewFixtures.Reference("cr_sql", null, ConnectionViewFixtures.Candidate("conn-1"));

        Assert.Equal(ConnectionBindingState.Unbound, ConnectionBindingPlanner.Observe(view).State);
        Assert.Equal(ConnectionBindingState.Bound, Plan(view).State);
        Assert.Equal("conn-1", Plan(view).ConnectionId);
    }

    [Fact]
    public void Observe_AnUndeclaredReferenceWithACandidate_IsUnboundRatherThanBound()
    {
        Assert.Equal(ConnectionBindingState.Unbound, ConnectionBindingPlanner.Observe(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_sql", Candidates = ImmutableArray.Create(ConnectionViewFixtures.Candidate("conn-1")), IsDeclared = false }).State);
    }

    [Fact]
    public void Observe_AReferenceWithNoConnector_IsUndeclared()
    {
        Assert.Equal(ConnectionBindingState.Undeclared, ConnectionBindingPlanner.Observe(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_orphan", IsDeclared = false }).State);
    }

    [Fact]
    public void Observe_ABindingTheCatalogCouldNotConfirm_IsNotAlreadyBound()
    {
        var observed = ConnectionBindingPlanner.Observe(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", BoundConnectionId = "conn-1", BoundConnectionExists = true, CatalogUnavailable = true });

        Assert.Equal(ConnectionBindingState.NoCandidate, observed.State);
        Assert.Null(observed.ConnectionId);
    }

    [Fact]
    public void Plan_ABindingWhoseConnectionIsGone_RebindsToAConnectionThePrincipalOwns()
    {
        var view = new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", BoundConnectionId = "conn-old", BoundConnectionExists = false, Candidates = ImmutableArray.Create(ConnectionViewFixtures.Candidate("conn-new")) };

        Assert.Equal(ConnectionBindingState.Unbound, ConnectionBindingPlanner.Observe(view).State);
        Assert.Equal("conn-new", Plan(view).ConnectionId);
    }

    [Fact]
    public void Observe_AWhitespaceBoundConnection_IsNotABinding()
    {
        Assert.Equal(ConnectionBindingState.NoCandidate, ConnectionBindingPlanner.Observe(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", BoundConnectionId = "  ", BoundConnectionExists = true }).State);
    }

    [Fact]
    public void Observe_KeepsOnlyConnectedCandidatesInConnectionIdOrder()
    {
        Assert.Equal(new[] { "conn-a", "conn-b" }, ConnectionBindingPlanner.Observe(ConnectionViewFixtures.Reference("cr_teams", null, ConnectionViewFixtures.Candidate("conn-b", "connected"), ConnectionViewFixtures.Candidate("conn-c", "Error"), ConnectionViewFixtures.Candidate("conn-a", "CONNECTED"))).Candidates.Select(candidate => candidate.Name));
    }

    [Theory]
    [InlineData(ConnectionBindingState.AlreadyBound, true)]
    [InlineData(ConnectionBindingState.Bound, true)]
    [InlineData(ConnectionBindingState.NoCandidate, false)]
    [InlineData(ConnectionBindingState.Unbound, false)]
    [InlineData(ConnectionBindingState.Undeclared, false)]
    public void IsResolved_HoldsOnlyForABinding(ConnectionBindingState state, bool resolved)
    {
        Assert.Equal(resolved, new ConnectionBindingDecision(ConnectionViewFixtures.Reference("cr_teams"), state, null, Array.Empty<ConnectionInstance>()).IsResolved);
    }

    [Fact]
    public void Observe_WithoutAView_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConnectionBindingPlanner.Observe(null!));
        Assert.Throws<ArgumentNullException>(() => ConnectionBindingPlanner.Plan(null!, ConnectionViewFixtures.Principal));
    }
}
