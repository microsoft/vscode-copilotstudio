// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using static Microsoft.CopilotStudio.Sync.UnitTests.ConnectionViewFixtures;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed record BindAndActivateOutcome(WorkflowActivationPassResult Result, FakeDataverseCloud Cloud, string Cache, string FreshCache, string Connections, string FreshConnections);

public class WorkflowActivationPassTests
{
    private const string SubmitExpenseId = "11111111-1111-1111-1111-111111111111";

    private const string NotifyOnRejectId = "22222222-2222-2222-2222-222222222222";

    private const string TeamsReference = "cree9_agent.shared_teams.x";

    private const string NotifyDefinition = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"cree9_agent.shared_teams.x"}}}}}
    """;

    private static readonly DirectoryPath Workspace = new("c:/test/workspace/");

    private static readonly AgentFilePath CloudCachePath = new(".mcs/botdefinition.json");

    private readonly Mock<IConnectionManagementService> _connections = new(MockBehavior.Strict);

    private readonly Mock<IWorkflowActivationService> _workflows = new(MockBehavior.Strict);

    private readonly InMemoryFileAccessorFactory _files = new();

    private readonly List<string> _calls = new();

    private readonly List<AgentConnectionView> _views = new();

    private readonly List<WorkflowStatusView> _workspaceWorkflows = new();

    private readonly List<WorkflowStatusView> _reported = new();

    private readonly List<ConnectionBindingRequest> _bound = new();

    private readonly List<WorkflowActivationRequest> _requested = new();

    private IReadOnlyList<AgentConnectionView>? _viewsAfterBinding;

    private IReadOnlyList<AgentConnectionView>? _viewsForWorkflows;

    private string? _refusal;

    public WorkflowActivationPassTests()
    {
        _connections.Setup(service => service.GetAgentConnectionViewsAsync(Workspace, It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<IConnectionCatalogClient>(), It.IsAny<PowerAppsContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Record<IReadOnlyList<AgentConnectionView>>("read", _views.ToArray()));
        _connections.Setup(service => service.WriteConnectionsCache(Workspace, It.IsAny<IReadOnlyList<AgentConnectionView>>()))
            .Callback(() => _calls.Add("cache"));
        _connections.Setup(service => service.ApplyConnectionBindingsAsync(Workspace, It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<IConnectionCatalogClient>(), It.IsAny<PowerAppsContext>(), It.IsAny<IReadOnlyList<ConnectionBindingRequest>>(), It.IsAny<CancellationToken>()))
            .Returns<DirectoryPath, DefinitionBase, ISyncDataverseClient, IConnectionCatalogClient, PowerAppsContext, IReadOnlyList<ConnectionBindingRequest>, CancellationToken>((_, _, _, _, _, bindings, _) =>
            {
                _bound.AddRange(bindings);
                return Task.FromResult(Record("bind", _viewsAfterBinding ?? _views.ToArray()));
            });
        _workflows.Setup(service => service.GetWorkflowStatusViews(Workspace, It.IsAny<IReadOnlyList<AgentConnectionView>>()))
            .Returns<DirectoryPath, IReadOnlyList<AgentConnectionView>>((_, views) =>
            {
                _viewsForWorkflows = views;
                return Record<IReadOnlyList<WorkflowStatusView>>("status", _workspaceWorkflows.ToArray());
            });
        _workflows.Setup(service => service.SetWorkflowActivationsAsync(Workspace, It.IsAny<IReadOnlyList<WorkflowActivationRequest>>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<CancellationToken>()))
            .Returns<DirectoryPath, IReadOnlyList<WorkflowActivationRequest>, ISyncDataverseClient, CancellationToken>((_, requests, _, _) =>
            {
                _requested.AddRange(requests);
                return Task.FromResult(Record("activate", new WorkflowActivationResult { Succeeded = _refusal is null, Message = _refusal, Workflows = _reported.ToImmutableArray() }));
            });
    }

    [Fact]
    public async Task RunAsync_ActivatesOnlyTheWorkflowsWhoseConnectionsAreBound()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));
        _workspaceWorkflows.Add(Workflow(NotifyOnRejectId, "NotifyOnReject", canEnable: false, connectionReferences: "cr_teams"));
        _reported.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true, WorkflowState.Activated));

        var result = await RunAsync();

        Assert.Null(result.Failure);
        Assert.Equal(Guid.Parse(SubmitExpenseId), Assert.Single(_requested).WorkflowId);
        Assert.True(_requested[0].Activate);
        Assert.Equal(new[] { "SubmitExpense" }, result.Activated);
        Assert.Equal(1, result.NotRunningCount);
        var notify = result.Workflows[0];
        Assert.Equal(("NotifyOnReject", WorkflowState.Draft, false), (notify.DisplayName, notify.State, notify.IsObserved));
        Assert.Equal(new[] { "cr_teams" }, notify.ConnectionReferenceLogicalNames);
        var submit = result.Workflows[1];
        Assert.Equal(("SubmitExpense", WorkflowState.Activated, true), (submit.DisplayName, submit.State, submit.IsObserved));
    }

    [Fact]
    public async Task RunAsync_WithNothingEligible_NeverAsksTheServiceAndObservesNothing()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "Blocked", canEnable: false, connectionReferences: "cr_teams"));

        var result = await RunAsync();

        Assert.Empty(_requested);
        Assert.DoesNotContain("activate", _calls);
        Assert.All(result.Workflows, workflow => Assert.False(workflow.IsObserved));
        Assert.Empty(result.Activated);
        Assert.Null(result.RefusalMessage);
        Assert.Equal(1, result.NotRunningCount);
    }

    [Fact]
    public async Task RunAsync_WhenTheConnectionsCouldNotBeListed_ReportsTheReferenceUnresolvedAndBindsNothing()
    {
        _views.Add(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", ConnectorName = "shared_teams", CatalogUnavailable = true });

        var result = await RunAsync();

        Assert.Empty(_bound);
        Assert.DoesNotContain("bind", _calls);
        var unresolved = Assert.Single(result.UnresolvedConnections);
        Assert.Equal(("cr_teams", ConnectionBindingState.NoCandidate), (unresolved.View.ConnectionReferenceLogicalName, unresolved.State));
    }

    [Fact]
    public async Task RunAsync_AVerifiedBinding_IsNeitherReboundNorReported()
    {
        _views.Add(Reference("cr_teams", "conn-teams", Candidate("conn-teams")));

        var result = await RunAsync();

        Assert.Empty(_bound);
        Assert.Empty(result.UnresolvedConnections);
    }

    [Fact]
    public async Task RunAsync_WhenTheServiceDeclinesToActivate_ReportsTheRefusalAndTheDraft()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));
        _reported.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true, WorkflowState.Draft));
        _refusal = "Workflow 'SubmitExpense' was kept as a draft because its connection could not be used.";

        var result = await RunAsync();

        Assert.Equal(_refusal, result.RefusalMessage);
        Assert.Empty(result.Activated);
        var workflow = Assert.Single(result.Workflows);
        Assert.Equal((WorkflowState.Draft, true), (workflow.State, workflow.IsObserved));
        Assert.Equal(1, result.NotRunningCount);
        Assert.Null(result.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunAsync_ABlankRefusal_IsNoRefusal(string message)
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));
        _refusal = message;

        Assert.Null((await RunAsync()).RefusalMessage);
    }

    [Fact]
    public async Task RunAsync_WhenTheServiceStartsOnlySomeWorkflows_NamesEachRequestedWorkflowItLeftStopped()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));
        _workspaceWorkflows.Add(Workflow(NotifyOnRejectId, " ", canEnable: true));
        _workspaceWorkflows.Add(Workflow("33333333-3333-3333-3333-333333333333", "Blocked", canEnable: false, connectionReferences: "cr_teams"));
        _workspaceWorkflows.Add(Workflow("not-a-guid", "Unidentified", canEnable: true));
        _reported.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true, WorkflowState.Activated));
        _reported.Add(Workflow(NotifyOnRejectId, " ", canEnable: true));
        _refusal = $"Workflow '{NotifyOnRejectId}' was kept as a draft because its connection could not be used.";

        Assert.Equal(new[] { NotifyOnRejectId }, (await RunAsync()).RefusedWorkflows);
    }

    [Fact]
    public async Task RunAsync_BindsAReferenceThatOneConnectionThePrincipalOwnsSatisfies()
    {
        _views.Add(Reference("cr_teams", null, Candidate("conn-1")));

        var result = await RunAsync();

        var binding = Assert.Single(_bound);
        Assert.Equal(("cr_teams", "conn-1"), (binding.ConnectionReferenceLogicalName, binding.ConnectionId));
        Assert.Empty(result.UnresolvedConnections);
    }

    [Fact]
    public async Task RunAsync_BindsOnlyTheConnectionThePrincipalOwns()
    {
        _views.Add(Reference("cr_teams", null, Candidate("conn-3", owner: "svc@contoso.com"), Candidate("conn-1"), Candidate("conn-2", owner: "svc@contoso.com")));

        var result = await RunAsync();

        Assert.Equal("conn-1", Assert.Single(_bound).ConnectionId);
        Assert.Empty(result.UnresolvedConnections);
    }

    [Fact]
    public async Task RunAsync_WhenEveryConnectionBelongsToSomebodyElse_BindsNothing()
    {
        _views.Add(Reference("cr_teams", null, Candidate("conn-2", owner: "svc@contoso.com"), Candidate("conn-3", owner: "alice@contoso.com")));

        var result = await RunAsync();

        Assert.Empty(_bound);
        var unresolved = Assert.Single(result.UnresolvedConnections);
        Assert.Equal(ConnectionBindingState.Unbound, unresolved.State);
        Assert.Equal(new[] { "conn-2", "conn-3" }, unresolved.Candidates.Select(candidate => candidate.Name));
    }

    [Fact]
    public async Task RunAsync_WithoutAPrincipal_BindsNothing()
    {
        _views.Add(Reference("cr_teams", null, Candidate("conn-1")));

        var result = await RunAsync(principal: null);

        Assert.Empty(_bound);
        Assert.Equal(ConnectionBindingState.Unbound, Assert.Single(result.UnresolvedConnections).State);
    }

    [Fact]
    public async Task RunAsync_DoesNotRebindAReferenceThatIsAlreadyBound()
    {
        _views.Add(Reference("cr_teams", "conn-1", Candidate("conn-1"), Candidate("conn-2")));

        await RunAsync();

        Assert.Empty(_bound);
    }

    [Fact]
    public async Task RunAsync_AnUndeclaredReferenceWithAConnectionThePrincipalOwns_IsBound()
    {
        _views.Add(new AgentConnectionView { ConnectionReferenceLogicalName = "cr_teams", ConnectorName = "shared_teams", Candidates = ImmutableArray.Create(Candidate("conn-1")), IsDeclared = false });

        var result = await RunAsync();

        Assert.Equal("conn-1", Assert.Single(_bound).ConnectionId);
        Assert.Empty(result.UnresolvedConnections);
    }

    [Fact]
    public async Task RunAsync_AfterBinding_ReadsTheWorkflowsAgainstTheViewsTheBindingReturned()
    {
        _views.Add(Reference("cr_teams", null, Candidate("conn-1")));
        _viewsAfterBinding = new[] { Reference("cr_teams", "conn-1", Candidate("conn-1")) };
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true, connectionReferences: "cr_teams"));

        await RunAsync();

        Assert.Same(_viewsAfterBinding, _viewsForWorkflows);
        Assert.Equal(new[] { "read", "cache", "bind", "status", "activate" }, _calls);
    }

    [Fact]
    public async Task RunAsync_WithoutBindings_RecordsTheConnectionsBeforeActivating()
    {
        _views.Add(Reference("cr_teams", "conn-1", Candidate("conn-1")));
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true, connectionReferences: "cr_teams"));

        await RunAsync();

        Assert.Equal(new[] { "read", "cache", "status", "activate" }, _calls);
        Assert.Equal("cr_teams", Assert.Single(_viewsForWorkflows!).ConnectionReferenceLogicalName);
    }

    [Fact]
    public async Task RunAsync_CountsUnresolvedReferencesAndNotRunningWorkflows()
    {
        _views.Add(Reference("cr_teams"));
        _views.Add(Reference("cr_spo"));
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: false, connectionReferences: "cr_teams"));
        _workspaceWorkflows.Add(Workflow(NotifyOnRejectId, "NotifyOnReject", canEnable: false, connectionReferences: "cr_spo"));

        var result = await RunAsync();

        Assert.Equal(new[] { "cr_spo", "cr_teams" }, result.UnresolvedConnections.Select(decision => decision.View.ConnectionReferenceLogicalName));
        Assert.Equal(2, result.NotRunningCount);
    }

    [Fact]
    public async Task RunAsync_WhenTheReadFails_ReturnsTheFailure()
    {
        var failure = new InvalidOperationException("the service is unavailable");
        _connections.Setup(service => service.GetAgentConnectionViewsAsync(Workspace, It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<IConnectionCatalogClient>(), It.IsAny<PowerAppsContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await RunAsync();

        Assert.Same(failure, result.Failure);
        Assert.Empty(result.Workflows);
        Assert.Empty(result.UnresolvedConnections);
    }

    [Fact]
    public async Task RunAsync_WhenBindingFails_ReturnsTheFailureWithoutActivating()
    {
        var failure = new InvalidOperationException("Dataverse request failed (403).");
        _views.Add(Reference("cr_teams", null, Candidate("conn-1")));
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));
        _connections.Setup(service => service.ApplyConnectionBindingsAsync(Workspace, It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<IConnectionCatalogClient>(), It.IsAny<PowerAppsContext>(), It.IsAny<IReadOnlyList<ConnectionBindingRequest>>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        Assert.Same(failure, (await RunAsync()).Failure);
        Assert.Empty(_requested);
    }

    [Fact]
    public async Task RunAsync_WhenActivationFails_ReturnsTheFailure()
    {
        var failure = new InvalidOperationException("Dataverse request failed (500).");
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));
        _workflows.Setup(service => service.SetWorkflowActivationsAsync(Workspace, It.IsAny<IReadOnlyList<WorkflowActivationRequest>>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        var result = await RunAsync();

        Assert.Same(failure, result.Failure);
        Assert.Empty(result.Workflows);
    }

    [Fact]
    public async Task RunAsync_WhenTheReadIsCancelled_Throws()
    {
        _connections.Setup(service => service.GetAgentConnectionViewsAsync(Workspace, It.IsAny<DefinitionBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<IConnectionCatalogClient>(), It.IsAny<PowerAppsContext>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() => RunAsync());
    }

    [Fact]
    public async Task RunAsync_WhenActivationIsCancelled_Throws()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));
        _workflows.Setup(service => service.SetWorkflowActivationsAsync(Workspace, It.IsAny<IReadOnlyList<WorkflowActivationRequest>>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => RunAsync());
    }

    [Fact]
    public async Task RunAsync_AnIdentifiedObservation_WinsOverASharedDisplayName()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "Shared", canEnable: true));
        _workspaceWorkflows.Add(Workflow(NotifyOnRejectId, "Shared", canEnable: true));
        _reported.Add(Workflow(SubmitExpenseId, "Shared", canEnable: true, WorkflowState.Activated));
        _reported.Add(Workflow(NotifyOnRejectId, "Shared", canEnable: true, WorkflowState.Draft));

        var result = await RunAsync();

        Assert.Equal(WorkflowState.Activated, Assert.Single(result.Workflows, workflow => workflow.WorkflowId == SubmitExpenseId).State);
        Assert.Equal(WorkflowState.Draft, Assert.Single(result.Workflows, workflow => workflow.WorkflowId == NotifyOnRejectId).State);
    }

    [Fact]
    public async Task RunAsync_ASharedDisplayNameTheServiceCannotIdentify_FallsBackToTheWorkspaceRecord()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "Shared", canEnable: true, WorkflowState.Activated));
        _workspaceWorkflows.Add(Workflow(NotifyOnRejectId, "shared", canEnable: true, WorkflowState.Draft));
        _reported.Add(Workflow(string.Empty, "Shared", canEnable: true, WorkflowState.Draft));

        var result = await RunAsync();

        Assert.All(result.Workflows, workflow => Assert.False(workflow.IsObserved));
        Assert.Equal(WorkflowState.Activated, Assert.Single(result.Workflows, workflow => workflow.WorkflowId == SubmitExpenseId).State);
        Assert.Equal(WorkflowState.Draft, Assert.Single(result.Workflows, workflow => workflow.WorkflowId == NotifyOnRejectId).State);
    }

    [Fact]
    public async Task RunAsync_AnIdentifiedObservation_WinsOverADisagreeingName()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "Renamed", canEnable: true));
        _reported.Add(Workflow(SubmitExpenseId, "Original", canEnable: true, WorkflowState.Activated));
        _reported.Add(Workflow(string.Empty, "Renamed", canEnable: true, WorkflowState.Draft));

        Assert.Equal(WorkflowState.Activated, Assert.Single((await RunAsync()).Workflows).State);
    }

    [Fact]
    public async Task RunAsync_AUniqueDisplayName_IsAttributedByName()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));
        _reported.Add(Workflow(string.Empty, "submitexpense", canEnable: true, WorkflowState.Activated));

        var workflow = Assert.Single((await RunAsync()).Workflows);

        Assert.Equal((WorkflowState.Activated, true), (workflow.State, workflow.IsObserved));
    }

    [Fact]
    public async Task RunAsync_ASuspendedReport_IsNoObservation()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true, WorkflowState.Draft));
        _reported.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true, WorkflowState.Suspended));

        var workflow = Assert.Single((await RunAsync()).Workflows);

        Assert.Equal((WorkflowState.Draft, false), (workflow.State, workflow.IsObserved));
    }

    [Theory]
    [InlineData(WorkflowState.Activated, WorkflowState.Activated)]
    [InlineData(WorkflowState.Draft, WorkflowState.Draft)]
    [InlineData(WorkflowState.Suspended, WorkflowState.Unknown)]
    [InlineData(WorkflowState.Unknown, WorkflowState.Unknown)]
    public async Task RunAsync_InAnAgentWorkspace_FallsBackToTheRecordedState(WorkflowState recorded, WorkflowState reported)
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: false, recorded));

        Assert.Equal(reported, Assert.Single((await RunAsync()).Workflows).State);
    }

    [Fact]
    public async Task RunAsync_InAStandaloneWorkflowProject_ReportsAnUnobservedWorkflowAsUnknown()
    {
        WorkflowWorkspace.EnsureProjectFile(_files.Create(Workspace));
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: false, WorkflowState.Activated));
        _workspaceWorkflows.Add(Workflow(NotifyOnRejectId, "NotifyOnReject", canEnable: true, WorkflowState.Draft));
        _reported.Add(Workflow(NotifyOnRejectId, "NotifyOnReject", canEnable: true, WorkflowState.Activated));

        var result = await RunAsync();

        Assert.Equal(WorkflowState.Unknown, Assert.Single(result.Workflows, workflow => workflow.WorkflowId == SubmitExpenseId).State);
        Assert.Equal(WorkflowState.Activated, Assert.Single(result.Workflows, workflow => workflow.WorkflowId == NotifyOnRejectId).State);
    }

    [Fact]
    public async Task RunAsync_SkipsWorkflowIdsThatAreNotGuids()
    {
        _workspaceWorkflows.Add(Workflow("not-a-guid", "Broken", canEnable: true));
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "SubmitExpense", canEnable: true));

        await RunAsync();

        Assert.Equal(Guid.Parse(SubmitExpenseId), Assert.Single(_requested).WorkflowId);
    }

    [Fact]
    public async Task RunAsync_WhenNoEligibleIdIsAGuid_StillAsksTheServiceForTheStates()
    {
        _workspaceWorkflows.Add(Workflow("not-a-guid", "Broken", canEnable: true));

        await RunAsync();

        Assert.Contains("activate", _calls);
        Assert.Empty(_requested);
    }

    [Fact]
    public async Task RunAsync_WithoutWorkflows_ReportsOnlyTheConnections()
    {
        _views.Add(Reference("cr_teams", null, Candidate("conn-1")));
        _views.Add(Reference("cr_spo"));

        var result = await RunAsync();

        Assert.Empty(result.Workflows);
        Assert.DoesNotContain("activate", _calls);
        Assert.Equal("conn-1", Assert.Single(_bound).ConnectionId);
        Assert.Equal("cr_spo", Assert.Single(result.UnresolvedConnections).View.ConnectionReferenceLogicalName);
        Assert.Equal(0, result.NotRunningCount);
    }

    [Fact]
    public async Task RunAsync_ABlankDisplayName_FallsBackToTheId()
    {
        _workspaceWorkflows.Add(new WorkflowStatusView { WorkflowId = SubmitExpenseId, DisplayName = " ", State = WorkflowState.Draft });

        Assert.Equal(SubmitExpenseId, Assert.Single((await RunAsync()).Workflows).DisplayName);
    }

    [Fact]
    public async Task RunAsync_OrdersTheWorkflowsByDisplayName()
    {
        _workspaceWorkflows.Add(Workflow(SubmitExpenseId, "beta", canEnable: false));
        _workspaceWorkflows.Add(Workflow(NotifyOnRejectId, "Alpha", canEnable: false));

        var result = await RunAsync();

        Assert.Equal(new[] { "Alpha", "beta" }, result.Workflows.Select(workflow => workflow.DisplayName));
    }

    [Fact]
    public async Task RunAsync_WithoutARequiredArgument_Throws()
    {
        var pass = CreatePass();
        var dataverse = new Mock<ISyncDataverseClient>().Object;
        var catalog = new Mock<IConnectionCatalogClient>().Object;
        var context = new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" };

        Assert.Equal("definition", (await Assert.ThrowsAsync<ArgumentNullException>(() => pass.RunAsync(Workspace, null!, dataverse, catalog, context, Principal, CancellationToken.None))).ParamName);
        Assert.Equal("dataverseClient", (await Assert.ThrowsAsync<ArgumentNullException>(() => pass.RunAsync(Workspace, new BotDefinition(), null!, catalog, context, Principal, CancellationToken.None))).ParamName);
        Assert.Equal("catalogClient", (await Assert.ThrowsAsync<ArgumentNullException>(() => pass.RunAsync(Workspace, new BotDefinition(), dataverse, null!, context, Principal, CancellationToken.None))).ParamName);
        Assert.Equal("catalogContext", (await Assert.ThrowsAsync<ArgumentNullException>(() => pass.RunAsync(Workspace, new BotDefinition(), dataverse, catalog, null!, Principal, CancellationToken.None))).ParamName);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Constructor_WithoutADependency_Throws()
    {
        Assert.Equal("connections", Assert.Throws<ArgumentNullException>(() => new WorkflowActivationPass(null!, _workflows.Object, _files)).ParamName);
        Assert.Equal("workflows", Assert.Throws<ArgumentNullException>(() => new WorkflowActivationPass(_connections.Object, null!, _files)).ParamName);
        Assert.Equal("fileAccessorFactory", Assert.Throws<ArgumentNullException>(() => new WorkflowActivationPass(_connections.Object, _workflows.Object, null!)).ParamName);
    }

    [Fact]
    public async Task RunAsync_AfterBindingAndActivating_LeavesTheCachesEqualToAFreshClone()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));

        var outcome = await BindAndActivateInMemoryAsync(factory, session.Root);

        Assert.Null(outcome.Result.Failure);
        Assert.Equal(new[] { "Notify" }, outcome.Result.Activated);
        Assert.Equal("conn-1", outcome.Cloud.ConnectionReferences[TeamsReference].ConnectionId);
        Assert.Equal(1, Assert.Single(outcome.Cloud.Workflows.Values).StateCode);
        Assert.Contains("conn-1", outcome.Cache, StringComparison.Ordinal);
        Assert.Equal(outcome.FreshCache, outcome.Cache);
        Assert.Equal(outcome.FreshConnections, outcome.Connections);
    }

    internal static async Task<BindAndActivateOutcome> BindAndActivateInMemoryAsync(IFileAccessorFactory factory, DirectoryPath root)
    {
        var workspace = root.GetChildDirectoryPath("Agent");
        var fresh = root.GetChildDirectoryPath("Fresh");
        var island = new Mock<IIslandControlPlaneService>();
        island.Setup(service => service.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cree9_agent")!, "token-1"));
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), factory, island.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var cloud = new FakeDataverseCloud();
        cloud.AddConnectionReference(TeamsReference, TeamsConnectorId);
        var workflowId = Guid.NewGuid();
        cloud.Workflows[workflowId] = new WorkflowMetadata { WorkflowId = workflowId, Name = "Notify", StateCode = 0, StatusCode = 1, ClientData = NotifyDefinition };
        var catalog = new Mock<IConnectionCatalogClient>();
        catalog.Setup(client => client.ListConnectionsAsync(It.IsAny<PowerAppsContext>(), "shared_teams", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Candidate("conn-2", owner: "svc@contoso.com"), Candidate("conn-1") });
        var context = new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" };
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), ComponentWriterDefensiveTests.CreateMockOperationContext(), cloud.Client.Object, syncInfo, CancellationToken.None);
        var result = await new WorkflowActivationPass(synchronizer, synchronizer, factory).RunAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), cloud.Client.Object, catalog.Object, context, Principal, CancellationToken.None);
        await synchronizer.CloneChangesAsync(fresh, new ReferenceTracker(), ComponentWriterDefensiveTests.CreateMockOperationContext(), cloud.Client.Object, syncInfo, CancellationToken.None);
        var freshViews = await synchronizer.GetAgentConnectionViewsAsync(fresh, await synchronizer.ReadWorkspaceDefinitionAsync(fresh, CancellationToken.None), cloud.Client.Object, catalog.Object, context, CancellationToken.None);
        var connections = synchronizer.ReadConnectionsCache(workspace)!;

        return new BindAndActivateOutcome(
            result,
            cloud,
            await factory.Create(workspace).ReadStringAsync(CloudCachePath, CancellationToken.None),
            await factory.Create(fresh).ReadStringAsync(CloudCachePath, CancellationToken.None),
            JsonSerializer.Serialize(new { connections.Connections, connections.Workflows }),
            JsonSerializer.Serialize(new { Connections = freshViews, Workflows = synchronizer.GetWorkflowStatusViews(fresh, freshViews) }));
    }

    private static WorkflowStatusView Workflow(string workflowId, string displayName, bool canEnable, WorkflowState state = WorkflowState.Draft, params string[] connectionReferences) => new()
    {
        WorkflowId = workflowId,
        DisplayName = displayName,
        FilePath = $"workflows/{displayName}/metadata.yml",
        State = state,
        ConnectionReferenceLogicalNames = connectionReferences.ToImmutableArray(),
        CanEnable = canEnable,
    };

    private T Record<T>(string call, T value)
    {
        _calls.Add(call);
        return value;
    }

    private WorkflowActivationPass CreatePass() => new(_connections.Object, _workflows.Object, _files);

    private Task<WorkflowActivationPassResult> RunAsync(string? principal = Principal) => CreatePass().RunAsync(Workspace, new BotDefinition(), new Mock<ISyncDataverseClient>(MockBehavior.Strict).Object, new Mock<IConnectionCatalogClient>(MockBehavior.Strict).Object, new PowerAppsContext { AccessToken = "token", EnvironmentId = "env" }, principal, CancellationToken.None);
}
