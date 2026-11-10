// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostPushStageTests
{
    private const string Marker = "stage-marker-5c1e";

    private const string WeatherReference = "new_sharedmsnweather_08889";

    private static readonly Guid ConnectorId = new("a1ac675b-8b84-416e-9450-add1b0db8624");

    private static readonly Guid PromptId = new("3b5436b4-d7b4-4389-96e8-107446c9094a");

    private static readonly string[] TypicalPushStages = { AgentPushStages.Agent, AgentPushStages.Components, AgentPushStages.Connectors, AgentPushStages.ConnectionReferences, AgentPushStages.Workflows, AgentPushStages.Prompts, AgentPushStages.Components, AgentPushStages.Activation, AgentPushStages.Components };

    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    public static TheoryData<string> Stages => new() { AgentPushStages.Agent, AgentPushStages.Connectors, AgentPushStages.ConnectionReferences, AgentPushStages.Workflows, AgentPushStages.Prompts, AgentPushStages.Components, AgentPushStages.Publish };

    public static TheoryData<Exception> ObserverFailures => new() { new InvalidOperationException(Marker), new OperationCanceledException(Marker), new NotSupportedException(Marker) };

    [Theory]
    [MemberData(nameof(Stages))]
    public async Task PushAsync_WhenAStageFailsUnexpectedly_NamesTheStageInOneDiagnosticWithoutTheFailureText(string stage)
    {
        var host = _cloud.CreateHost(_files);
        var snapshot = Arrange(stage);
        var stages = new List<string>();

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => host.PushAsync(snapshot, HostedCloud.Target(SchemaName), HostedCloud.Options() with { StageStarted = stages.Add }, _authentication, CancellationToken.None));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal(AgentWorkspaceHostErrorCodes.PushFailed, failure.Code);
        Assert.Equal((AgentSyncDiagnosticCodes.PushStageFailed, stage, (string?)null, (string?)null), (diagnostic.Code, diagnostic.Subject, diagnostic.Detail, diagnostic.Path));
        Assert.Equal($"The push stopped at the '{stage}' stage.", diagnostic.Message);
        Assert.Equal(stage, stages[^1]);
        Assert.NotNull(failure.InnerException);
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task PushAsync_WhenTheActivationPassFails_NamesTheActivationStage()
    {
        var host = _cloud.CreateHost(_files);
        var snapshot = Arrange(string.Empty);

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => host.PushAsync(snapshot, HostedCloud.Target(SchemaName), HostedCloud.Options() with
        {
            StageStarted = stage =>
            {
                if (stage == AgentPushStages.Activation)
                {
                    _cloud.Cloud.QueryThrows = new InvalidOperationException(Marker);
                }
            },
        }, _authentication, CancellationToken.None));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal((AgentWorkspaceHostErrorCodes.PushFailed, AgentSyncDiagnosticCodes.PushStageFailed, AgentPushStages.Activation), (failure.Code, diagnostic.Code, diagnostic.Subject));
        Assert.StartsWith("The content was pushed, but the workflows could not be activated:", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushWorkflowAsync_WhenTheWorkflowCannotBeWritten_NamesTheWorkflowsStage()
    {
        var workflows = new Mock<IStandaloneWorkflowDataverseClient>();
        workflows.Setup(client => client.GetWorkflowAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException(Marker));
        var host = _cloud.CreateHost(_files, configure: services => services.AddSingleton(workflows.Object));

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => host.PushWorkflowAsync(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", Guid.NewGuid()), new WorkflowSyncTarget(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, null), HostedCloud.Options(), _authentication, CancellationToken.None));

        var diagnostic = Assert.Single(failure.Diagnostics);
        Assert.Equal((AgentWorkspaceHostErrorCodes.PushFailed, AgentSyncDiagnosticCodes.PushStageFailed, AgentPushStages.Workflows), (failure.Code, diagnostic.Code, diagnostic.Subject));
        Assert.DoesNotContain(Marker, diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloneAsync_WhenItFailsUnexpectedly_AddsNoStageDiagnostic()
    {
        var botId = _cloud.SeedAgent(SchemaName, "Contoso Helpdesk");
        _cloud.Cloud.Client.Setup(client => client.GetAgentInfoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException(Marker));

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _cloud.CreateHost(_files).CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName, botId.ToString()), "Agent", new Dictionary<string, string>(), Array.Empty<string>()), _authentication, CancellationToken.None));

        Assert.Equal(AgentWorkspaceHostErrorCodes.CloneFailed, failure.Code);
        Assert.Empty(failure.Diagnostics);
    }

    [Fact]
    public async Task PushAsync_ReportsEachStageOfTheSharedPushInOrder()
    {
        var stages = new List<string>();
        var snapshot = Arrange(string.Empty);
        var call = new AgentWorkspaceCallFactory("Microsoft.CopilotStudio.Sync.UnitTests", new StubHandlerFactory(), services =>
        {
            _cloud.Register(services);
            services.AddSingleton<Microsoft.CopilotStudio.McsCore.IFileAccessorFactory>(_files);
        }).Open(HostedCloud.Organization, _authentication);

        try
        {
            WorkspaceSnapshotFiles.Write(call.Files, call.Root, snapshot);
            var attached = await call.AttachService.AttachAsync(new WorkspaceAttachRequest { WorkspaceFolder = call.FolderOf("Agent"), DataverseEndpoint = HostedCloud.Organization, EnvironmentId = HostedCloud.EnvironmentId, TenantId = HostedCloud.TenantId, DisplayName = "Contoso Helpdesk", SchemaName = SchemaName, CreateIfMissing = true, PushAuxiliaryContent = false }, call.Dataverse, CancellationToken.None);
            await call.PushService.PushAsync(new WorkspacePushRequest { WorkspaceFolder = call.FolderOf("Agent"), StageStarted = stages.Add }, call.Dataverse, CancellationToken.None);
        }
        finally
        {
            await call.DisposeAsync();
        }

        Assert.Equal(new[] { AgentPushStages.Components, AgentPushStages.Connectors, AgentPushStages.ConnectionReferences, AgentPushStages.Workflows, AgentPushStages.Prompts, AgentPushStages.Components }, stages);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushAsync_TellsTheObserverEachStageItMovesIntoInOrderAndThePublishStageOnlyWhenItPublishes(bool publish)
    {
        var host = _cloud.CreateHost(_files);
        var snapshot = Arrange(string.Empty);
        var stages = new List<string>();

        var result = await host.PushAsync(snapshot, HostedCloud.Target(SchemaName), HostedCloud.Options() with { Publish = publish, StageStarted = stages.Add }, _authentication, CancellationToken.None);

        Assert.Equal((publish, 0), (result.Published, result.Diagnostics.Count));
        Assert.Equal(TypicalPushStages.Concat(publish ? new[] { AgentPushStages.Publish } : Array.Empty<string>()), stages);
    }

    [Fact]
    public async Task PushAsync_OfAnAgentAndItsCollection_TellsTheObserverTheCollectionsStagesBeforeTheAgents()
    {
        var stages = new List<string>();

        Assert.True((await _cloud.CreateHost(_files).PushAsync(ClassicAgentFixtures.Cc1(), HostedCloud.Target(ClassicAgentFixtures.Cc1Schema), HostedCloud.Options() with { StageStarted = stages.Add }, _authentication, CancellationToken.None)).Published);
        Assert.Equal(new[] { AgentPushStages.Agent, AgentPushStages.Components, AgentPushStages.Connectors, AgentPushStages.ConnectionReferences, AgentPushStages.Components, AgentPushStages.Connectors, AgentPushStages.ConnectionReferences, AgentPushStages.Workflows, AgentPushStages.Prompts, AgentPushStages.Components, AgentPushStages.Activation, AgentPushStages.Components, AgentPushStages.Publish }, stages);
    }

    [Fact]
    public async Task PushWorkflowAsync_TellsTheObserverTheWorkflowsStageAndThenTheActivationStage()
    {
        var stages = new List<string>();

        await _cloud.CreateHost(_files).PushWorkflowAsync(AgentWorkspaceHostWorkflowTests.Scaffold("Weather", Guid.NewGuid()), new WorkflowSyncTarget(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, null), HostedCloud.Options() with { StageStarted = stages.Add }, _authentication, CancellationToken.None);

        Assert.Equal(new[] { AgentPushStages.Workflows, AgentPushStages.Activation }, stages);
    }

    [Theory]
    [MemberData(nameof(ObserverFailures))]
    public async Task PushAsync_WhenTheObserverThrows_PushesAndPublishesThroughEveryStageAsIfNobodyWatched(Exception observerFailure)
    {
        var host = _cloud.CreateHost(_files);
        var snapshot = Arrange(string.Empty);
        var stages = new List<string>();

        var result = await host.PushAsync(snapshot, HostedCloud.Target(SchemaName), HostedCloud.Options() with
        {
            StageStarted = stage =>
            {
                stages.Add(stage);
                throw observerFailure;
            },
        }, _authentication, CancellationToken.None);

        Assert.True(result.Published);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(TypicalPushStages.Append(AgentPushStages.Publish), stages);
        Assert.Single(_cloud.Published);
        Assert.Equal(0, _files.LiveCount);
    }

    internal static AgentWorkspaceSnapshot Scaffold() => Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, AgentAuthoringModes.Classic, instructions: "Be helpful.");

    private AgentWorkspaceSnapshot Arrange(string stage)
    {
        var snapshot = WithWorkflow(With(Scaffold(), "Agent/connectionreferences.mcs.yml", Declaration(WeatherReference, "shared_msnweather")), "Weather", Guid.NewGuid(), Definition("shared_msnweather", WeatherReference));
        snapshot = With(snapshot, $"Agent/connectors/ContosoApi-{ConnectorId}/metadata.yml", $"{{\"connectorid\":\"{ConnectorId}\",\"name\":\"ContosoApi\",\"displayname\":\"Contoso API\",\"connectorinternalid\":\"shared_contosoapi\",\"openapidefinition\":\"connectors/ContosoApi-{ConnectorId}/openapidefinition.json\",\"connectortype\":0}}");
        snapshot = With(snapshot, $"Agent/connectors/ContosoApi-{ConnectorId}/openapidefinition.json", "{\"swagger\":\"2.0\"}");
        snapshot = With(snapshot, $"Agent/prompts/Summarize-{PromptId}/metadata.yml", $"aIModelId: {PromptId}\nname: Summarize\n");
        snapshot = With(snapshot, $"Agent/prompts/Summarize-{PromptId}/prompt.json", "{\"name\":\"Summarize\"}");

        switch (stage)
        {
            case AgentPushStages.Agent:
                _cloud.CreateThrows = new InvalidOperationException(Marker);
                break;
            case AgentPushStages.Connectors:
                _cloud.Cloud.Client.Setup(client => client.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>())).ThrowsAsync(new TaskCanceledException(Marker));
                break;
            case AgentPushStages.ConnectionReferences:
                _cloud.Cloud.ProvisioningThrows = new InvalidOperationException(Marker);
                break;
            case AgentPushStages.Workflows:
                _cloud.Cloud.Client.Setup(client => client.UpdateWorkflowAsync(It.IsAny<Guid?>(), It.IsAny<WorkflowMetadata?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException(Marker));
                break;
            case AgentPushStages.Prompts:
                _cloud.Cloud.Client.Setup(client => client.UpsertAIPromptAsync(It.IsAny<Guid?>(), It.IsAny<AIPromptMetadata?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException(Marker));
                break;
            case AgentPushStages.Components:
                _cloud.Island.SaveFailures.Enqueue(new InvalidOperationException(Marker));
                break;
            case AgentPushStages.Publish:
                _cloud.PublishThrows = new TaskCanceledException(Marker);
                break;
        }

        return snapshot;
    }
}
