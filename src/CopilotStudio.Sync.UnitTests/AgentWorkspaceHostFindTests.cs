// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Net;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostFindTests
{
    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    private readonly AgentWorkspaceHost _host;

    public AgentWorkspaceHostFindTests() => _host = _cloud.CreateHost(_files);

    [Fact]
    public async Task FindAgentAsync_OfABotCarryingTheSchemaName_ReturnsItsIdentifierFromOneReleasedSessionWithoutChangingAnything()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        _cloud.SeedAgent("con_other", "Other Agent");

        Assert.Equal(botId.ToString(), await FindAsync(HostedCloud.Target(SchemaName)));
        Assert.Equal(new[] { nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync) }, _cloud.Cloud.Client.Invocations.Select(invocation => invocation.Method.Name).Where(name => name != nameof(ISyncDataverseClient.SetDataverseUrl)));
        Assert.Empty(_cloud.Island.Service.Invocations);
        Assert.Single(_files.Opened);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task FindAgentAsync_WhenNoBotCarriesTheSchemaName_ReturnsNullAndCreatesNone()
    {
        _cloud.SeedAgent("con_other", "Other Agent");

        Assert.Null(await FindAsync(HostedCloud.Target(SchemaName)));
        Assert.Equal(new[] { "con_other" }, _cloud.CreatedAgents);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task FindAgentAsync_ResolvesNoServiceTheLookupDoesNotUse()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        var host = _cloud.CreateHost(_files, configure: services =>
        {
            foreach (var unused in new[] { typeof(IWorkspaceSynchronizer), typeof(IWorkspaceRetargetService), typeof(IOperationContextProvider), typeof(WorkspaceAttachService), typeof(WorkflowActivationPass), typeof(IStandaloneWorkflowSynchronizer), typeof(ISyncComponentCollectionDataverseClient), typeof(ISyncComponentCollectionLinkDataverseClient), typeof(IAgentPublishService), typeof(IWorkspacePushService) })
            {
                services.AddSingleton(unused, _ => throw new InvalidOperationException($"The lookup resolved {unused.Name}."));
            }
        });

        Assert.Equal(botId.ToString(), await host.FindAgentAsync(HostedCloud.Target(SchemaName), _authentication, CancellationToken.None));
    }

    [Fact]
    public async Task FindAgentAsync_WithoutATarget_ThrowsBeforeAnyCloudCall()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => FindAsync(null!));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_authentication.Audiences);
        Assert.Empty(_files.Opened);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" ", "8c1c1d0e-0f5e-4b8e-9d0c-7b7d6c5b4a39")]
    public async Task FindAgentAsync_WithoutASchemaName_ThrowsBeforeAnyCloudCall(string schemaName, string? botId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => FindAsync(HostedCloud.Target(schemaName, botId)));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_authentication.Audiences);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task FindAgentAsync_WhenTheEnvironmentFailsTheLookup_ThrowsLookupFailedAndReleasesTheSession()
    {
        var refusal = new DataverseRequestException(HttpStatusCode.Forbidden, "The caller may not read bots.");
        _cloud.Cloud.Client.Setup(client => client.GetAgentIdBySchemaNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(refusal);

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => FindAsync(HostedCloud.Target(SchemaName)));

        Assert.Equal(AgentWorkspaceHostErrorCodes.LookupFailed, failure.Code);
        Assert.Same(refusal, failure.InnerException);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task FindAgentAsync_WithACanceledToken_TouchesNoEnvironment()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _host.FindAgentAsync(HostedCloud.Target(SchemaName), _authentication, new CancellationToken(canceled: true)));

        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task FindWorkflowAsync_OfAWorkflowTheEnvironmentHolds_ReturnsItsIdentifierFromOneReadInOneReleasedSessionWithoutChangingAnything()
    {
        var workflowId = Guid.NewGuid();
        _cloud.StandaloneWorkflows.Seed(new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "Weather" });
        _cloud.StandaloneWorkflows.Seed(new SyncDataverseClient.WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Other" });

        Assert.Equal(workflowId.ToString(), await FindWorkflowAsync(Workflow(workflowId.ToString())));
        Assert.Equal(new[] { "get" }, _cloud.StandaloneWorkflows.Calls.Where(call => call != "target"));
        Assert.Empty(_cloud.DataverseCalls());
        Assert.Empty(_cloud.Island.Service.Invocations);
        Assert.Single(_files.Opened);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task FindWorkflowAsync_WhenTheEnvironmentHoldsNoSuchWorkflow_ReturnsNullAndWritesNothing()
    {
        _cloud.StandaloneWorkflows.Seed(new SyncDataverseClient.WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Other" });

        Assert.Null(await FindWorkflowAsync(Workflow(Guid.NewGuid().ToString())));
        Assert.Equal(new[] { "get" }, _cloud.StandaloneWorkflows.Calls.Where(call => call != "target"));
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task FindWorkflowAsync_WithoutATarget_ThrowsBeforeAnyCloudCall()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => FindWorkflowAsync(null!));

        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_authentication.Audiences);
        Assert.Empty(_files.Opened);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("weather")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task FindWorkflowAsync_WithoutAValidWorkflowIdentifier_ThrowsBeforeAnyCloudCall(string? workflowId)
    {
        Assert.Equal("target", (await Assert.ThrowsAsync<ArgumentException>(() => FindWorkflowAsync(Workflow(workflowId)))).ParamName);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_authentication.Audiences);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task FindWorkflowAsync_WhenTheEnvironmentFailsTheLookup_ThrowsLookupFailedAndReleasesTheSession()
    {
        var refusal = new DataverseRequestException(HttpStatusCode.Forbidden, "The caller may not read workflows.");
        _cloud.StandaloneWorkflows.GetFailure = refusal;

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => FindWorkflowAsync(Workflow(Guid.NewGuid().ToString())));

        Assert.Equal(AgentWorkspaceHostErrorCodes.LookupFailed, failure.Code);
        Assert.Same(refusal, failure.InnerException);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task FindWorkflowAsync_WithACanceledToken_TouchesNoEnvironment()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _host.FindWorkflowAsync(Workflow(Guid.NewGuid().ToString()), _authentication, new CancellationToken(canceled: true)));

        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_files.Opened);
    }

    private static WorkflowSyncTarget Workflow(string? workflowId) => new(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, workflowId);

    private Task<string?> FindAsync(AgentSyncTarget target) => _host.FindAgentAsync(target, _authentication, CancellationToken.None);

    private Task<string?> FindWorkflowAsync(WorkflowSyncTarget target) => _host.FindWorkflowAsync(target, _authentication, CancellationToken.None);
}
