// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkflowActivationPlannerTests
{
    private static WorkflowMetadata Workflow(int stateCode, params string[] connectionReferences) =>
        new() { WorkflowId = Guid.NewGuid(), Name = "WF", StateCode = stateCode, StatusCode = stateCode == 1 ? 2 : 1, ConnectionReferences = [.. connectionReferences] };

    private static ISyncDataverseClient ClientReturning(params ConnectionReferenceInfo[] references)
    {
        var client = new Mock<ISyncDataverseClient>();

        client.Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(references);

        return client.Object;
    }

    private static ConnectionReferenceInfo Reference(string logicalName, string connectionId) =>
        new() { ConnectionReferenceLogicalName = logicalName, ConnectorId = "shared_x", ConnectionId = connectionId };

    [Fact]
    public async Task DraftUnboundActivationsAsync_WhenAReferenceHasNoConnection_DraftsTheWorkflow()
    {
        var workflow = Workflow(1, "new_weather");

        var downgraded = await WorkflowActivationPlanner.DraftUnboundActivationsAsync([workflow], ClientReturning(Reference("new_weather", string.Empty)), CancellationToken.None);

        Assert.Same(workflow, Assert.Single(downgraded));
        Assert.Equal(0, workflow.StateCode);
        Assert.Equal(1, workflow.StatusCode);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_WhenTheReferenceIsMissingEntirely_DraftsTheWorkflow()
    {
        var workflow = Workflow(1, "new_weather");

        await WorkflowActivationPlanner.DraftUnboundActivationsAsync([workflow], ClientReturning(), CancellationToken.None);

        Assert.Equal(0, workflow.StateCode);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_WhenEveryReferenceIsBound_LeavesTheWorkflowActivated()
    {
        var workflow = Workflow(1, "new_weather", "new_teams");

        var downgraded = await WorkflowActivationPlanner.DraftUnboundActivationsAsync(
            [workflow],
            ClientReturning(Reference("new_weather", "conn-1"), Reference("new_teams", "conn-2")),
            CancellationToken.None);

        Assert.Empty(downgraded);
        Assert.Equal(1, workflow.StateCode);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_WhenOnlyOneOfTwoReferencesIsBound_DraftsTheWorkflow()
    {
        var workflow = Workflow(1, "new_weather", "new_teams");

        await WorkflowActivationPlanner.DraftUnboundActivationsAsync(
            [workflow],
            ClientReturning(Reference("new_weather", "conn-1"), Reference("new_teams", string.Empty)),
            CancellationToken.None);

        Assert.Equal(0, workflow.StateCode);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_MatchesLogicalNamesWithoutRegardToCase()
    {
        var workflow = Workflow(1, "New_Weather");

        await WorkflowActivationPlanner.DraftUnboundActivationsAsync([workflow], ClientReturning(Reference("new_weather", "conn-1")), CancellationToken.None);

        Assert.Equal(1, workflow.StateCode);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_WhenTheWorkflowIsAlreadyADraft_LeavesItAlone()
    {
        var workflow = Workflow(0, "new_weather");
        var client = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

        Assert.Empty(await WorkflowActivationPlanner.DraftUnboundActivationsAsync([workflow], client.Object, CancellationToken.None));
        Assert.Equal(0, workflow.StateCode);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_WhenNothingBindsAConnection_DoesNotCallDataverse()
    {
        var client = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

        Assert.Empty(await WorkflowActivationPlanner.DraftUnboundActivationsAsync([Workflow(1)], client.Object, CancellationToken.None));
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_AsksForEachLogicalNameOnlyOnce()
    {
        var client = new Mock<ISyncDataverseClient>();
        var requested = new List<string>();

        client.Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, CancellationToken>((names, _) => requested.AddRange(names))
            .ReturnsAsync([Reference("new_weather", "conn-1")]);

        await WorkflowActivationPlanner.DraftUnboundActivationsAsync([Workflow(1, "new_weather"), Workflow(1, "New_Weather")], client.Object, CancellationToken.None);

        Assert.Equal(["new_weather"], requested);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_DraftsOnlyTheWorkflowsThatAreUnbound()
    {
        var bound = Workflow(1, "new_teams");
        var unbound = Workflow(1, "new_weather");

        var downgraded = await WorkflowActivationPlanner.DraftUnboundActivationsAsync(
            [bound, unbound],
            ClientReturning(Reference("new_teams", "conn-1"), Reference("new_weather", string.Empty)),
            CancellationToken.None);

        Assert.Same(unbound, Assert.Single(downgraded));
        Assert.Equal(1, bound.StateCode);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_IgnoresBlankLogicalNames()
    {
        var workflow = Workflow(1, "new_weather", "   ");

        await WorkflowActivationPlanner.DraftUnboundActivationsAsync([workflow], ClientReturning(Reference("new_weather", "conn-1")), CancellationToken.None);

        Assert.Equal(1, workflow.StateCode);
    }

    [Fact]
    public void HasConnectionReferences_IsFalseWhenEveryNameIsBlank()
    {
        Assert.False(WorkflowActivationPlanner.HasConnectionReferences(Workflow(1, "  ", string.Empty)));
        Assert.True(WorkflowActivationPlanner.HasConnectionReferences(Workflow(1, "new_weather")));
    }

    [Fact]
    public async Task ActivateWhenBoundAsync_StartsTheBoundWorkflowAndDraftsTheUnboundOne()
    {
        var bound = Workflow(0, "new_teams");
        var unbound = Workflow(1, "new_weather");

        await WorkflowActivationPlanner.ActivateWhenBoundAsync(
            [bound, unbound],
            ClientReturning(Reference("new_teams", "conn-1"), Reference("new_weather", string.Empty)),
            CancellationToken.None);

        Assert.Equal(1, bound.StateCode);
        Assert.Equal(2, bound.StatusCode);
        Assert.Equal(0, unbound.StateCode);
        Assert.Equal(1, unbound.StatusCode);
    }

    [Fact]
    public async Task ActivateWhenBoundAsync_IgnoresBlankLogicalNamesJustAsDraftingDoes()
    {
        var activating = Workflow(0, "new_weather", "   ");
        var drafting = Workflow(1, "new_weather", "   ");

        var client = ClientReturning(Reference("new_weather", "conn-1"));

        await WorkflowActivationPlanner.ActivateWhenBoundAsync([activating], client, CancellationToken.None);
        var downgraded = await WorkflowActivationPlanner.DraftUnboundActivationsAsync([drafting], client, CancellationToken.None);

        Assert.Equal(1, activating.StateCode);
        Assert.Empty(downgraded);
        Assert.Equal(1, drafting.StateCode);
    }

    [Fact]
    public async Task ActivateWhenBoundAsync_WhenNothingBindsAConnection_StartsWithoutCallingDataverse()
    {
        var workflow = Workflow(0);
        var client = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

        await WorkflowActivationPlanner.ActivateWhenBoundAsync([workflow], client.Object, CancellationToken.None);

        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task ActivateWhenBoundAsync_RejectsMissingArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkflowActivationPlanner.ActivateWhenBoundAsync(null!, ClientReturning(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkflowActivationPlanner.ActivateWhenBoundAsync([], null!, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something else entirely")]
    public void IsConnectionAuthorizationFailure_IsFalseForAnythingElse(string? failure) =>
        Assert.False(WorkflowActivationPlanner.IsConnectionAuthorizationFailure(failure));

    [Theory]
    [InlineData("ConnectionAuthorizationFailed")]
    [InlineData("Failed to update workflow: connectionauthorizationfailed. Connection 'new_teams'")]
    public void IsConnectionAuthorizationFailure_IsTrueWhateverTheCasing(string failure) =>
        Assert.True(WorkflowActivationPlanner.IsConnectionAuthorizationFailure(failure));

    [Fact]
    public async Task TryStopAsync_WhenTheEnvironmentRefuses_ReportsWhyInsteadOfThrowing()
    {
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        client.Setup(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.Forbidden, "Forbidden"));

        var outcome = await WorkflowActivationPlanner.TryStopAsync(Guid.NewGuid(), client.Object, CancellationToken.None);

        Assert.False(outcome.Changed);
        Assert.True(outcome.Refused);
        Assert.Contains("Forbidden", outcome.Failure);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.BadGateway)]
    [InlineData(System.Net.HttpStatusCode.GatewayTimeout)]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    [InlineData(System.Net.HttpStatusCode.RequestTimeout)]
    public async Task TryStopAsync_WhenTheServiceNeverDecided_SaysTheStateIsUnknown(System.Net.HttpStatusCode status)
    {
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        client.Setup(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(status, "the gateway gave up"));

        var outcome = await WorkflowActivationPlanner.TryStopAsync(Guid.NewGuid(), client.Object, CancellationToken.None);

        Assert.False(outcome.Changed);
        Assert.False(outcome.Refused);
        Assert.True(outcome.IsUnknown);
    }

    [Fact]
    public async Task TryStopAsync_WhenTheOutcomeIsUnknown_Propagates()
    {
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        client.Setup(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("the connection was reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => WorkflowActivationPlanner.TryStopAsync(Guid.NewGuid(), client.Object, CancellationToken.None));
    }

    [Fact]
    public async Task TryStopAsync_WhenTheWorkflowStops_ReportsNothing()
    {
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        Assert.True((await WorkflowActivationPlanner.TryStopAsync(Guid.NewGuid(), client.Object, CancellationToken.None)).Changed);
        client.Verify(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryStopAsync_DoesNotSwallowCancellation()
    {
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        client.Setup(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => WorkflowActivationPlanner.TryStopAsync(Guid.NewGuid(), client.Object, CancellationToken.None));
    }

    [Fact]
    public async Task TryStartAsync_WhenTheEnvironmentRefuses_ReportsWhyWithoutEstablishingADraft()
    {
        var workflow = Workflow(1, "new_weather");
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        client.Setup(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.BadRequest, "ConnectionAuthorizationFailed"));

        var outcome = await WorkflowActivationPlanner.TryStartAsync(workflow, client.Object, CancellationToken.None);

        Assert.True(outcome.Refused);
        Assert.Contains("ConnectionAuthorizationFailed", outcome.Failure);
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task TryStartAsync_WhenAnAlreadyRunningWorkflowIsRefused_KeepsItRunning()
    {
        var workflow = Workflow(1, "new_weather");
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        client.Setup(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.Forbidden, "Forbidden"));

        Assert.True((await WorkflowActivationPlanner.TryStartAsync(workflow, client.Object, CancellationToken.None)).Refused);
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task TryStartAsync_WhenTheServiceNeverDecided_LeavesTheRequestedStateAlone()
    {
        var workflow = Workflow(1, "new_weather");
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        client.Setup(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.GatewayTimeout, "the gateway gave up"));

        var outcome = await WorkflowActivationPlanner.TryStartAsync(workflow, client.Object, CancellationToken.None);

        Assert.True(outcome.IsUnknown);
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task TryStartAsync_WhenTheWorkflowStarts_ReportsNothingAndMarksItRunning()
    {
        var workflow = Workflow(0, "new_weather");

        Assert.True((await WorkflowActivationPlanner.TryStartAsync(workflow, new Mock<IStandaloneWorkflowDataverseClient>().Object, CancellationToken.None)).Changed);
        Assert.Equal(1, workflow.StateCode);
        Assert.Equal(2, workflow.StatusCode);
    }

    [Fact]
    public async Task TryStartAsync_DoesNotSwallowCancellation()
    {
        var client = new Mock<IStandaloneWorkflowDataverseClient>();

        client.Setup(c => c.SetWorkflowStateAsync(It.IsAny<Guid>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => WorkflowActivationPlanner.TryStartAsync(Workflow(1), client.Object, CancellationToken.None));
    }

    [Fact]
    public async Task TryStartAsync_RejectsMissingArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkflowActivationPlanner.TryStartAsync(null!, new Mock<IStandaloneWorkflowDataverseClient>().Object, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkflowActivationPlanner.TryStartAsync(Workflow(1), null!, CancellationToken.None));
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_RejectsMissingArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkflowActivationPlanner.DraftUnboundActivationsAsync(null!, ClientReturning(), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkflowActivationPlanner.DraftUnboundActivationsAsync([], (ISyncDataverseClient)null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => WorkflowActivationPlanner.DraftStandaloneUnboundActivationsAsync([], null!, CancellationToken.None));
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_ReadsTheSameReferencesThroughEitherContract()
    {
        var throughSync = Workflow(1, "new_weather");
        var throughStandalone = Workflow(1, "new_weather");

        var standalone = new Mock<IStandaloneWorkflowDataverseClient>();
        standalone
            .Setup(c => c.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([Reference("new_weather", string.Empty)]);

        Assert.Single(await WorkflowActivationPlanner.DraftUnboundActivationsAsync([throughSync], ClientReturning(Reference("new_weather", string.Empty)), CancellationToken.None));
        Assert.Single(await WorkflowActivationPlanner.DraftStandaloneUnboundActivationsAsync([throughStandalone], standalone.Object, CancellationToken.None));
        Assert.Equal(throughSync.StateCode, throughStandalone.StateCode);
    }

    [Fact]
    public async Task DraftUnboundActivationsAsync_AcceptsAConcreteClientWithoutACast()
    {
        var client = new SyncDataverseClient(Mock.Of<Microsoft.Agents.Platform.Content.Abstractions.IDataverseHttpClientAccessor>());

        Assert.Empty(await WorkflowActivationPlanner.DraftUnboundActivationsAsync([Workflow(1)], client, CancellationToken.None));
    }
}
