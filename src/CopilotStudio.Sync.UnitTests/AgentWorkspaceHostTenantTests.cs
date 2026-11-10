// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.Sync.Hosting;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostTenantTests
{
    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    private readonly AgentWorkspaceHost _host;

    public AgentWorkspaceHostTenantTests() => _host = _cloud.CreateHost(_files);

    [Theory]
    [InlineData("find")]
    [InlineData("find-workflow")]
    [InlineData("clone")]
    [InlineData("push")]
    [InlineData("clone-workflow")]
    [InlineData("push-workflow")]
    public async Task Operation_WithoutATenant_ThrowsTargetInvalidBeforeAnyCloudCall(string operation)
    {
        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(async () => await RunWithoutTenantAsync(operation));

        Assert.Equal(AgentWorkspaceHostErrorCodes.TargetInvalid, failure.Code);
        Assert.Equal(AgentSyncDiagnosticCodes.TenantMissing, Assert.Single(failure.Diagnostics).Code);
        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_cloud.Island.Service.Invocations);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_authentication.Audiences);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task CloneAsync_GivesTheCallersTenantToEveryAgentAndCollectionRead()
    {
        _cloud.Link(_cloud.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting")), _cloud.SeedCollection("cree9_tools", "Shared Tools", PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Lookup", "Lookup")));

        await _host.CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), _authentication, CancellationToken.None);

        AssertEveryIslandCallCarriesTheTenant();
    }

    [Fact]
    public async Task PushAsync_GivesTheCallersTenantToEveryAgentAndCollectionReadAndSave()
    {
        await _host.PushAsync(ScaffoldWithCollections(), HostedCloud.Target(SchemaName), HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Contains(_cloud.Island.Service.Invocations, invocation => invocation.Method.Name == nameof(IIslandControlPlaneService.SaveChangesAsync));
        AssertEveryIslandCallCarriesTheTenant();
    }

    private void AssertEveryIslandCallCarriesTheTenant()
    {
        var contexts = _cloud.Island.Service.Invocations.Select(invocation => Assert.IsAssignableFrom<AuthoringOperationContextBase>(invocation.Arguments[0])).ToArray();

        Assert.Contains(contexts, context => context is AuthoringOperationContext);
        Assert.Contains(contexts, context => context is BotComponentCollectionAuthoringOperationContext);
        Assert.All(contexts, context => Assert.Equal(HostedCloud.TenantId, context.OrganizationInfo.TenantId));
        Assert.All(contexts, context => Assert.Equal(HostedCloud.TenantId, context.ImpersonatedUser?.AadTenantId));
    }

    private async Task RunWithoutTenantAsync(string operation)
    {
        var agent = HostedCloud.Target(SchemaName) with { TenantId = Guid.Empty };
        var workflow = new WorkflowSyncTarget(HostedCloud.Organization, HostedCloud.EnvironmentId, Guid.Empty, null);

        switch (operation)
        {
            case "find":
                await _host.FindAgentAsync(agent, _authentication, CancellationToken.None);
                break;
            case "find-workflow":
                await _host.FindWorkflowAsync(workflow with { WorkflowId = Guid.NewGuid().ToString() }, _authentication, CancellationToken.None);
                break;
            case "clone":
                await _host.CloneAsync(new AgentCloneRequest(agent, "Agent", new Dictionary<string, string>(), Array.Empty<string>()), _authentication, CancellationToken.None);
                break;
            case "push":
                await _host.PushAsync(Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic), agent, HostedCloud.Options(), _authentication, CancellationToken.None);
                break;
            case "clone-workflow":
                await _host.CloneWorkflowAsync(new WorkflowCloneRequest(workflow with { WorkflowId = Guid.NewGuid().ToString() }, "Weather"), _authentication, CancellationToken.None);
                break;
            default:
                await _host.PushWorkflowAsync(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", Guid.NewGuid()), workflow, HostedCloud.Options(), _authentication, CancellationToken.None);
                break;
        }
    }
}
