// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AgentPushPublishTimeTests
{
    private const string SettingsFile = "Agent/settings.mcs.yml";

    private static readonly DateTimeOffset BotPublishedOn = new(AuthoredSettingsPushTests.BotPublishedOn);

    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    [Fact]
    public async Task PushAsync_WithoutPublishing_ReportsWhenTheBotWasLastPublishedAndNotThePublishTimeTheFolderDeclares()
    {
        var botId = AuthoredSettingsPushTests.Published(_cloud, _cloud.SeedAgent(SchemaName, "Contoso Helpdesk"));
        var snapshot = AgentWorkspaceHostPushStageTests.Scaffold();

        var result = await _cloud.CreateHost(_files).PushAsync(With(snapshot, SettingsFile, AuthoredSettingsPushTests.WithAuthoredPublishedOn(Text(snapshot, SettingsFile))), HostedCloud.Target(SchemaName, botId.ToString()), HostedCloud.Options() with { Publish = false }, _authentication, CancellationToken.None);

        Assert.Equal((false, BotPublishedOn), (result.Published, result.PublishedOn));
        Assert.Equal(new[] { "GetAgentIdBySchemaNameAsync", "GetProvisioningStatusAsync", "GetSolutionVersionsAsync" }, _cloud.DataverseCalls());
    }

    [Fact]
    public async Task PushAsync_ThatPublishes_ReportsWhenItsPublishFinished()
    {
        var botId = AuthoredSettingsPushTests.Published(_cloud, _cloud.SeedAgent(SchemaName, "Contoso Helpdesk"));

        var result = await _cloud.CreateHost(_files).PushAsync(AgentWorkspaceHostPushStageTests.Scaffold(), HostedCloud.Target(SchemaName, botId.ToString()), HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Equal((true, new DateTimeOffset(2026, 10, 25, 19, 1, 12, TimeSpan.Zero)), (result.Published, result.PublishedOn));
        Assert.Equal(new[] { "GetAgentIdBySchemaNameAsync", "GetProvisioningStatusAsync", "GetSolutionVersionsAsync", "GetProvisioningStatusAsync", "GetLastPublishOperationAsync", "PublishAgentAsync", "GetLastPublishOperationAsync" }, _cloud.DataverseCalls());
    }

    [Fact]
    public async Task PushAsync_WhosePublishFinishesWithAnotherStatus_ReportsWhenTheBotWasLastPublished()
    {
        var botId = AuthoredSettingsPushTests.Published(_cloud, _cloud.SeedAgent(SchemaName, "Contoso Helpdesk"));
        _cloud.PublishFinishesWith = "Failed";

        var result = await _cloud.CreateHost(_files).PushAsync(AgentWorkspaceHostPushStageTests.Scaffold(), HostedCloud.Target(SchemaName, botId.ToString()), HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Equal((false, BotPublishedOn), (result.Published, result.PublishedOn));
    }

    [Fact]
    public async Task PushAsync_ThatCreatesTheBotWithoutPublishing_ReportsNoPublishTime()
    {
        var result = await _cloud.CreateHost(_files).PushAsync(AgentWorkspaceHostPushStageTests.Scaffold(), HostedCloud.Target(SchemaName), HostedCloud.Options() with { Publish = false }, _authentication, CancellationToken.None);

        Assert.Equal(SchemaName, Assert.Single(_cloud.CreatedAgents));
        Assert.Null(result.PublishedOn);
        Assert.Equal(new[] { "GetAgentIdBySchemaNameAsync", "CreateNewAgentAsync", "GetProvisioningStatusAsync", "GetSolutionVersionsAsync" }, _cloud.DataverseCalls());
    }

    [Fact]
    public async Task WorkspacePushAsync_OfAPublishedBot_ReportsWhenTheBotWasLastPublished()
    {
        var botId = AuthoredSettingsPushTests.Published(_cloud, _cloud.SeedAgent(SchemaName, "Contoso Helpdesk"));
        var snapshot = AgentWorkspaceHostPushStageTests.Scaffold();
        var call = new AgentWorkspaceCallFactory("Microsoft.CopilotStudio.Sync.UnitTests", new StubHandlerFactory(), services =>
        {
            _cloud.Register(services);
            services.AddSingleton<IFileAccessorFactory>(_files);
        }).Open(HostedCloud.Organization, _authentication);

        try
        {
            WorkspaceSnapshotFiles.Write(call.Files, call.Root, snapshot);
            await call.AttachService.AttachAsync(new WorkspaceAttachRequest { WorkspaceFolder = call.FolderOf("Agent"), DataverseEndpoint = HostedCloud.Organization, EnvironmentId = HostedCloud.EnvironmentId, TenantId = HostedCloud.TenantId, DisplayName = "Contoso Helpdesk", SchemaName = SchemaName, ExpectedAgentId = botId }, call.Dataverse, CancellationToken.None);
            var attachCalls = _cloud.DataverseCalls().Count;

            var result = await call.PushService.PushAsync(new WorkspacePushRequest { WorkspaceFolder = call.FolderOf("Agent") }, call.Dataverse, CancellationToken.None);

            Assert.Equal(BotPublishedOn, result.PublishedOn);
            Assert.Equal(new[] { "GetProvisioningStatusAsync" }, _cloud.DataverseCalls().Skip(attachCalls));
        }
        finally
        {
            await call.DisposeAsync();
        }
    }

}
