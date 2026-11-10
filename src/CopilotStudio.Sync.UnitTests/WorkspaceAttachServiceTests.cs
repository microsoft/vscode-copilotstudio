// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed record AttachBaseline(string? ChangeToken, bool UpdateWorkspaceDirectory, AgentSyncInfo SyncInfo, CloudFlowMetadata? FlowMetadata, ImmutableArray<AIPromptMetadata> Prompts, bool SyncCustomConnectors, bool SyncWorkflowsAndPrompts);

internal sealed record AttachOutcome(WorkspaceAttachResult Result, FakeDataverseCloud Cloud, string Cache, string FreshCache, bool RetargetBackupExists, AgentSyncInfo SavedSyncInfo);

internal sealed record ConnectableClone(HostedCloud Environment, WorkspaceSynchronizer Synchronizer, WorkspaceAttachService Service, WorkspaceConnectRequest Request, DirectoryPath AgentFolder, DirectoryPath ToolsFolder, Guid AgentId, Guid ToolsId, IReadOnlyList<KeyValuePair<string, string>> ClonedCache);

public class WorkspaceAttachServiceTests
{
    private const string SchemaName = "con_contosohelpdesk";

    private const string ToolsSchemaName = "cree9_tools";

    internal const string WeatherReference = "new_sharedmsnweather_08889";

    private const string WeatherDeclaration = """
    connectionReferences:
      - connectionReferenceLogicalName: new_sharedmsnweather_08889
        connectorId: /providers/Microsoft.PowerApps/apis/shared_msnweather

    """;

    internal const string WeatherDefinition = """
    {"properties":{"connectionReferences":{"shared_msnweather":{"api":{"name":"shared_msnweather"},"connection":{"connectionReferenceLogicalName":"new_sharedmsnweather_08889"}}}}}
    """;

    private static readonly DirectoryPath Workspace = new("operation/Contoso Helpdesk/");

    private static readonly DirectoryPath ToolsFolder = new("operation/Shared Tools/");

    private static readonly IReadOnlyDictionary<string, DirectoryPath> Tools = new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = ToolsFolder };

    private static readonly string[] CloudReads =
    {
        nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync),
        nameof(ISyncDataverseClient.GetAgentInfoAsync),
        nameof(ISyncDataverseClient.GetSolutionVersionsAsync),
        nameof(ISyncDataverseClient.DownloadAllWorkflowsForAgentAsync),
        nameof(ISyncDataverseClient.GetConnectionReferencesByLogicalNamesAsync),
        nameof(ISyncDataverseClient.GetConnectorsByInternalIdPrefixAsync),
        nameof(ISyncDataverseClient.DownloadAllAIPromptsForAgentAsync),
        nameof(ISyncDataverseClient.DownloadAIPromptsByModelIdsAsync),
        nameof(ISyncComponentCollectionLinkDataverseClient.GetComponentCollectionsBySchemaNamesAsync),
    };

    private static readonly Uri DataverseEndpoint = new("https://contoso.crm.dynamics.com/");

    private static readonly Guid TenantId = Guid.Parse("99999999-8888-7777-6666-555555555555");

    private static readonly Guid ExistingAgentId = Guid.Parse("cccccccc-dddd-eeee-ffff-000000000000");

    private static readonly Guid WorkflowId = Guid.Parse("12121212-3434-5656-7878-909090909090");

    private static readonly AgentFilePath CloudCachePath = new(".mcs/botdefinition.json");

    private static readonly AgentFilePath ChangeTokenPath = new(".mcs/changetoken.txt");

    private static readonly AgentFilePath SyncInfoPath = new(".mcs/conn.json");

    private static readonly AgentFilePath RetargetBackupPath = new(".mcs/.retarget-backup.json");

    private readonly ProductionFileAccessorFactory _files = new();

    private readonly FakeDataverseCloud _cloud = new();

    private readonly HostedCloud _environment = new();

    private readonly Mock<IWorkspaceSynchronizer> _synchronizer = new(MockBehavior.Strict);

    private readonly Mock<IWorkspaceRetargetService> _retarget = new(MockBehavior.Strict);

    private readonly Mock<IWorkspaceCloudCacheWriter> _cacheWriter = new(MockBehavior.Strict);

    private readonly Mock<IOperationContextProvider> _contexts = new(MockBehavior.Strict);

    private readonly WorkspaceSynchronizer _provisioner = ComponentWriterDefensiveTests.CreateSyncInfrastructure().sync;

    private readonly AuthoringOperationContext _context = ComponentWriterDefensiveTests.CreateMockOperationContext();

    private readonly RemoteBindingSnapshot _snapshot = new(ImmutableArray.Create(new RemoteBindingFile(SyncInfoPath, new byte[] { 1 })));

    private readonly List<string> _calls = new();

    private readonly List<WorkflowResponse> _workflowResponses = new();

    private readonly List<AIPromptResponse> _promptResponses = new();

    private readonly List<(DirectoryPath Folder, AgentSyncInfo SyncInfo)> _cacheWrites = new();

    private readonly Dictionary<DirectoryPath, AgentSyncInfo> _bindings = new();

    private readonly List<(string Call, bool OverwriteRemoteChanges)> _overwrites = new();

    private readonly IFileAccessor _workspace;

    private Func<DirectoryPath, Task> _cacheWritten = _ => Task.CompletedTask;

    private IReadOnlyList<string> _scaffolded;

    private bool _connected;

    private DefinitionBase _definition = new BotDefinition();

    private Exception? _definitionThrows;

    private Exception? _validationThrows;

    private Exception? _baselineThrows;

    private CloudFlowMetadata _reportedFlowMetadata = new();

    private ImmutableArray<AIPromptMetadata> _promptMetadata = ImmutableArray<AIPromptMetadata>.Empty;

    private IReadOnlyDictionary<string, Guid> _pushedConnectorIds = new Dictionary<string, Guid>();

    private IReadOnlyList<string> _connectorFailureReasons = Array.Empty<string>();

    private IReadOnlyDictionary<string, Guid>? _provisionedWith;

    private (Guid? AgentId, WorkflowActivationMode Mode)? _workflowUpsert;

    private Guid? _promptUpsertAgentId;

    private AttachBaseline? _baseline;

    private AgentSyncInfo? _saved;

    private AgentSyncInfo? _contextRequestedFor;

    private int? _dataverseCallsBeforeValidation;

    public WorkspaceAttachServiceTests()
    {
        _workspace = _files.Create(Workspace);
        _scaffolded = AgentScaffolder.Scaffold(_workspace, AgentAuthoringModes.CliCopilot, "Contoso Helpdesk", SchemaName, "Be helpful.");

        _synchronizer.Setup(synchronizer => synchronizer.IsSyncInfoAvailable(It.IsAny<DirectoryPath>())).Returns<DirectoryPath>(folder => _bindings.ContainsKey(folder) || (folder == Workspace && _connected));
        _synchronizer.Setup(synchronizer => synchronizer.GetSyncInfoAsync(It.IsAny<DirectoryPath>())).Returns<DirectoryPath>(folder => Task.FromResult(_bindings[folder]));
        _synchronizer.Setup(synchronizer => synchronizer.ReadWorkspaceDefinitionAsync(Workspace, It.IsAny<CancellationToken>(), false)).Returns(() => Record(nameof(IWorkspaceSynchronizer.ReadWorkspaceDefinitionAsync), _definitionThrows is null ? Task.FromResult(_definition) : Task.FromException<DefinitionBase>(_definitionThrows)));
        _synchronizer.Setup(synchronizer => synchronizer.ThrowIfWorkspaceInvalidAsync(Workspace, It.IsAny<DefinitionBase>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            _dataverseCallsBeforeValidation = _cloud.Client.Invocations.Count;
            return Record(nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync), _validationThrows is null ? Task.CompletedTask : Task.FromException(_validationThrows));
        });
        _synchronizer.Setup(synchronizer => synchronizer.ClearComponentSyncBaselines(Workspace)).Callback(() => _calls.Add(nameof(IWorkspaceSynchronizer.ClearComponentSyncBaselines)));
        _synchronizer.Setup(synchronizer => synchronizer.PushCustomConnectorsAsync(Workspace, _cloud.Client.Object, It.IsAny<CancellationToken>(), It.IsAny<bool>())).Returns<DirectoryPath, ISyncDataverseClient, CancellationToken, bool>((_, _, _, overwriteRemoteChanges) =>
        {
            _overwrites.Add((nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), overwriteRemoteChanges));
            return Record(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), Task.FromResult(new CustomConnectorPushResult { PushedRowIds = _pushedConnectorIds, FailureReasons = _connectorFailureReasons }));
        });
        _synchronizer.Setup(synchronizer => synchronizer.ProvisionConnectionReferencesAsync(It.IsAny<DefinitionBase>(), _cloud.Client.Object, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, Guid>?>())).Returns<DefinitionBase, ISyncDataverseClient, CancellationToken, IReadOnlyDictionary<string, Guid>?>((definition, client, cancellationToken, pushedConnectorIds) =>
        {
            _provisionedWith = pushedConnectorIds;
            return Record(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), _provisioner.ProvisionConnectionReferencesAsync(definition, client, cancellationToken, pushedConnectorIds));
        });
        _synchronizer.Setup(synchronizer => synchronizer.UpsertWorkflowForAgentAsync(Workspace, _cloud.Client.Object, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<WorkflowActivationMode>())).Returns<DirectoryPath, ISyncDataverseClient, Guid?, CancellationToken, WorkflowActivationMode>((_, _, agentId, _, activationMode) =>
        {
            _workflowUpsert = (agentId, activationMode);
            return Record(nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync), Task.FromResult((_workflowResponses.ToImmutableArray(), _reportedFlowMetadata)));
        });
        _synchronizer.Setup(synchronizer => synchronizer.UpsertAIPromptsForAgentAsync(Workspace, _cloud.Client.Object, It.IsAny<Guid?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>())).Returns<DirectoryPath, ISyncDataverseClient, Guid?, CancellationToken, bool>((_, _, agentId, _, overwriteRemoteChanges) =>
        {
            _promptUpsertAgentId = agentId;
            _overwrites.Add((nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), overwriteRemoteChanges));
            return Record(nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), Task.FromResult((_promptResponses.ToImmutableArray(), _promptMetadata)));
        });
        _synchronizer.Setup(synchronizer => synchronizer.SaveSyncInfoAsync(Workspace, It.IsAny<AgentSyncInfo>())).Returns<DirectoryPath, AgentSyncInfo>((_, syncInfo) =>
        {
            _saved = syncInfo;
            return Record(nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), Task.CompletedTask);
        });
        _retarget.Setup(retarget => retarget.ResetRemoteBindingState(Workspace)).Returns(() => Record(nameof(IWorkspaceRetargetService.ResetRemoteBindingState), _snapshot));
        _retarget.Setup(retarget => retarget.PersistRetargetBackup(Workspace, _snapshot)).Callback(() => _calls.Add(nameof(IWorkspaceRetargetService.PersistRetargetBackup)));
        _retarget.Setup(retarget => retarget.FinalizeRetarget(Workspace, true)).Returns(() => Record(nameof(IWorkspaceRetargetService.FinalizeRetarget), true));
        _retarget.Setup(retarget => retarget.RestoreRemoteBindingState(Workspace, _snapshot)).Callback(() => _calls.Add(nameof(IWorkspaceRetargetService.RestoreRemoteBindingState)));
        _retarget.Setup(retarget => retarget.ClearRetargetBackup(Workspace)).Callback(() => _calls.Add(nameof(IWorkspaceRetargetService.ClearRetargetBackup)));
        _retarget.Setup(retarget => retarget.SyncWorkspaceAsync(Workspace, _context, It.IsAny<string?>(), It.IsAny<bool>(), _cloud.Client.Object, It.IsAny<AgentSyncInfo>(), It.IsAny<CloudFlowMetadata?>(), It.IsAny<CancellationToken>(), It.IsAny<ImmutableArray<AIPromptMetadata>>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>())).Returns<DirectoryPath, AuthoringOperationContextBase, string?, bool, ISyncDataverseClient, AgentSyncInfo, CloudFlowMetadata?, CancellationToken, ImmutableArray<AIPromptMetadata>, bool, bool, bool>((_, _, changeToken, updateWorkspaceDirectory, _, syncInfo, flowMetadata, _, prompts, syncCustomConnectors, syncWorkflowsAndPrompts, _) =>
        {
            _baseline = new AttachBaseline(changeToken, updateWorkspaceDirectory, syncInfo, flowMetadata, prompts, syncCustomConnectors, syncWorkflowsAndPrompts);
            return Record(nameof(IWorkspaceRetargetService.SyncWorkspaceAsync), _baselineThrows is null ? Task.FromResult(new WorkspaceSyncInfo()) : Task.FromException<WorkspaceSyncInfo>(_baselineThrows));
        });
        _contexts.Setup(contexts => contexts.GetAsync(It.IsAny<AgentSyncInfo>())).Returns<AgentSyncInfo>(syncInfo =>
        {
            _contextRequestedFor = syncInfo;
            return Task.FromResult<AuthoringOperationContextBase>(_context);
        });
        _synchronizer.Setup(synchronizer => synchronizer.SaveSyncInfoAsync(It.Is<DirectoryPath>(folder => folder != Workspace), It.IsAny<AgentSyncInfo>())).Returns(() => Record(nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), Task.CompletedTask));
        _cacheWriter.Setup(writer => writer.WriteCloudCacheAsync(It.IsAny<DirectoryPath>(), _context, _environment.Cloud.Client.Object, It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).Returns<DirectoryPath, AuthoringOperationContextBase, ISyncDataverseClient, AgentSyncInfo, CancellationToken>(async (folder, _, _, syncInfo, _) =>
        {
            _calls.Add($"{nameof(IWorkspaceCloudCacheWriter.WriteCloudCacheAsync)} {folder}");
            _cacheWrites.Add((folder, syncInfo));
            await WriteAsync(_files.Create(folder), CloudCachePath.ToString(), "{}");
            await _cacheWritten(folder);
        });
    }

    [Fact]
    public async Task AttachAsync_ValidatesTheWorkspaceBeforeAnyDataverseCall()
    {
        await AttachAsync();

        Assert.Equal(0, _dataverseCallsBeforeValidation);
    }

    [Fact]
    public async Task AttachAsync_WhenTheWorkspaceIsInvalid_ThrowsTheValidationFailureAndSendsNothing()
    {
        _validationThrows = new WorkspaceValidationException(ImmutableArray.Create(new WorkspaceDiagnostic("tools/lookup_order.tool.yml", "The file still contains merge conflict markers.", 12, 1, WorkspaceDiagnosticKind.MergeConflict)));

        Assert.Same(_validationThrows, await Assert.ThrowsAsync<WorkspaceValidationException>(() => AttachAsync()));
        Assert.Empty(_cloud.Client.Invocations);
        Assert.Equal(new[] { nameof(IWorkspaceSynchronizer.ReadWorkspaceDefinitionAsync), nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync) }, _calls);
    }

    [Fact]
    public async Task AttachAsync_WhenTheDefinitionCannotBeRead_PropagatesBeforeTouchingTheTarget()
    {
        _definitionThrows = new InvalidDataException("The cloud cache is corrupt.");

        Assert.Same(_definitionThrows, await Assert.ThrowsAsync<InvalidDataException>(() => AttachAsync()));
        Assert.Empty(_cloud.Client.Invocations);
    }

    [Fact]
    public async Task AttachAsync_WithNoDefinitionToRead_AttachesWithoutCheckingOrProvisioning()
    {
        _definitionThrows = new FileNotFoundException(".mcs/botdefinition.json was not found. Please resync.");

        Assert.Equal(FakeDataverseCloud.CreatedAgentId, (await AttachAsync()).AgentId);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync), _calls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), _calls);
        Assert.Contains(nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), _calls);
        Assert.Empty(_cloud.EnsuredLogicalNames);
    }

    [Fact]
    public async Task AttachAsync_WithNoDefinitionToRead_StillProvisionsAndSeedsTheClassicDeclarations()
    {
        _definitionThrows = new FileNotFoundException(".mcs/botdefinition.json was not found. Please resync.");
        await WriteAsync(_workspace, "connectionreferences.mcs.yml", WeatherDeclaration);

        await AttachAsync();

        Assert.Equal(new[] { WeatherReference }, _cloud.EnsuredLogicalNames);
        Assert.Equal(_cloud.ConnectionReferences[WeatherReference].ConnectionReferenceId, Assert.Single(_baseline!.FlowMetadata!.ConnectionReferences).Id.Value);
    }

    [Fact]
    public async Task AttachAsync_PushesConnectorsThenReferencesWorkflowsAndPromptsBeforeTheBaselineAndTheBinding()
    {
        await WriteAsync(_workspace, "connectionreferences.mcs.yml", WeatherDeclaration);

        await AttachAsync();

        Assert.Equal(
            new[]
            {
                nameof(IWorkspaceSynchronizer.ReadWorkspaceDefinitionAsync),
                nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync),
                nameof(IWorkspaceSynchronizer.ClearComponentSyncBaselines),
                nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync),
                nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync),
                nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync),
                nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync),
                nameof(IWorkspaceRetargetService.SyncWorkspaceAsync),
                nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync),
            },
            _calls);
    }

    [Fact]
    public async Task AttachAsync_WithoutAuxiliaryContent_WritesOnlyTheBaselineAndTheBinding()
    {
        await WriteAsync(_workspace, "connectionreferences.mcs.yml", WeatherDeclaration);
        _promptMetadata = ImmutableArray.Create(new AIPromptMetadata { AIModelId = Guid.NewGuid(), Name = "Summarize" });
        _connectorFailureReasons = new[] { "Failed to push custom connector 'Contoso API': The definition is not valid." };

        var result = await AttachAsync(Request(pushAuxiliaryContent: false));

        Assert.Equal(new[] { nameof(IWorkspaceSynchronizer.ReadWorkspaceDefinitionAsync), nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync), nameof(IWorkspaceSynchronizer.ClearComponentSyncBaselines), nameof(IWorkspaceRetargetService.SyncWorkspaceAsync), nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync) }, _calls);
        Assert.Null(_baseline!.FlowMetadata);
        Assert.Empty(_baseline.Prompts);
        Assert.Empty(_cloud.EnsuredLogicalNames);
        Assert.Empty(result.ConnectorFailureReasons);
        Assert.Empty(result.WorkflowResponses);
        Assert.Empty(result.PromptResponses);
        Assert.Same(result.SyncInfo, _saved);
    }

    [Fact]
    public async Task AttachAsync_PushesTheConnectorsAndPromptsOverWhatChangedInTheCloud()
    {
        await AttachAsync();

        Assert.Equal(new[] { (nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), true), (nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), true) }, _overwrites);
    }

    [Fact]
    public async Task AttachAsync_ReportsWhyEachConnectorWasRefusedWithoutTheRawResponse()
    {
        _connectorFailureReasons = new[] { $"Failed to push custom connector 'Contoso API': {DataverseFailureTextTests.Failure().Message}", "Failed to push custom connector 'Fabrikam API': it changed in the cloud since it was last synced." };

        var result = await AttachAsync();

        Assert.Equal(new[] { $"Failed to push custom connector 'Contoso API': {DataverseFailureTextTests.ReportedDescription}", "Failed to push custom connector 'Fabrikam API': it changed in the cloud since it was last synced." }, result.ConnectorFailureReasons);
    }

    [Fact]
    public async Task AttachAsync_RepointsTheDeclaredReferencesAtTheConnectorsPushedToTheTarget()
    {
        _pushedConnectorIds = new Dictionary<string, Guid> { ["shared_cr123-5fcontoso-5f1234567890abcdef"] = Guid.NewGuid() };
        await WriteAsync(_workspace, "connectionreferences.mcs.yml", WeatherDeclaration);

        await AttachAsync();

        Assert.Same(_pushedConnectorIds, _provisionedWith);
    }

    [Theory]
    [InlineData("connectionreferences.mcs.yml")]
    [InlineData("infrastructure/connections/new_sharedmsnweather_08889.sync.yaml")]
    public async Task AttachAsync_SeedsTheBaselineWithTheDeclaredReferenceAsTheTargetRecordsIt(string declarationPath)
    {
        await WriteAsync(_workspace, declarationPath, WeatherDeclaration);

        await AttachAsync();

        var seeded = Assert.Single(_baseline!.FlowMetadata!.ConnectionReferences);
        Assert.Equal(_cloud.ConnectionReferences[WeatherReference].ConnectionReferenceId, seeded.Id.Value);
        Assert.Equal(1, seeded.Version);
    }

    [Fact]
    public async Task AttachAsync_SeedsTheProvisionedReferencesAheadOfThoseTheWorkflowUpsertReported()
    {
        await WriteAsync(_workspace, "infrastructure/connections/new_sharedteams_777c895a.sync.yaml", "connectionReferences:\n  - connectionReferenceLogicalName: new_sharedteams_777c895a\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_teams\n");
        await WriteAsync(_workspace, "infrastructure/connections/crf9a_nb1.cr.shared_sharepointonline.a972.sync.yaml", "connectionReferences:\n  - connectionReferenceLogicalName: crf9a_nb1.cr.shared_sharepointonline.a972\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_sharepointonline\n");
        _reportedFlowMetadata = new CloudFlowMetadata { ConnectionReferences = ImmutableArray.Create(WorkspaceSynchronizer.ToConnectionReference(_cloud.AddConnectionReference("cree9_agent.shared_office365.x", "/providers/Microsoft.PowerApps/apis/shared_office365"))) };

        await AttachAsync();

        Assert.Equal(new[] { "crf9a_nb1.cr.shared_sharepointonline.a972", "new_sharedteams_777c895a", "cree9_agent.shared_office365.x" }, _baseline!.FlowMetadata!.ConnectionReferences.Select(reference => reference.ConnectionReferenceLogicalName.Value));
    }

    [Fact]
    public async Task AttachAsync_WithNoDeclarations_ProvisionsNothingAndSeedsWhatTheWorkflowUpsertReported()
    {
        await AttachAsync();

        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), _calls);
        Assert.Empty(_cloud.EnsuredLogicalNames);
        Assert.Same(_reportedFlowMetadata, _baseline!.FlowMetadata);
    }

    [Fact]
    public async Task AttachAsync_WhenTheTargetDoesNotConfirmADeclaration_ReportsTheAgentAndWritesNoBaseline()
    {
        await WriteAsync(_workspace, "connectionreferences.mcs.yml", WeatherDeclaration);
        _cloud.OmitProvisionedReferences = true;

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.Equal(WorkspaceAttachFailure.ReattachFailed, failure.Failure);
        Assert.Equal(FakeDataverseCloud.CreatedAgentId, failure.AgentId);
        Assert.IsType<DeclaredConnectionReferenceException>(failure.InnerException);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync), _calls);
        Assert.Null(_baseline);
        Assert.Null(_saved);
    }

    [Fact]
    public async Task AttachAsync_UpsertsWorkflowsAsDraftsAndPromptsForTheAttachedAgent()
    {
        await AttachAsync();

        Assert.Equal(FakeDataverseCloud.CreatedAgentId, _workflowUpsert!.Value.AgentId);
        Assert.Equal(WorkflowActivationMode.DraftWhenConnectionReferencesExist, _workflowUpsert.Value.Mode);
        Assert.Equal(FakeDataverseCloud.CreatedAgentId, _promptUpsertAgentId);
    }

    [Theory]
    [InlineData(AgentAuthoringModes.CliCopilot, AuthoringShape.CliCopilot)]
    [InlineData(AgentAuthoringModes.Classic, AuthoringShape.Classic)]
    public async Task AttachAsync_CreatesTheAgentWithTheShapeOfTheWorkspace(string template, AuthoringShape shape)
    {
        RemoveScaffold();
        _scaffolded = AgentScaffolder.Scaffold(_workspace, template, "Contoso Helpdesk", SchemaName, "Be helpful.");

        var result = await AttachAsync();

        _cloud.Client.Verify(client => client.CreateNewAgentAsync("Contoso Helpdesk", SchemaName, shape, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(FakeDataverseCloud.CreatedAgentId, result.AgentId);
        Assert.Equal(SchemaName, result.SchemaName);
        Assert.True(result.AgentCreated);
        Assert.False(result.Retargeted);
        Assert.False(result.AdoptedExistingAgent);
    }

    [Theory]
    [InlineData(null, SchemaName)]
    [InlineData(SchemaName, SchemaName)]
    [InlineData("con_contosohelpdesk1", "con_contosohelpdesk1")]
    public async Task AttachAsync_NeverRewritesTheWorkspaceFromTheBotItCreatedAndKeepsTheAuthoredSettingsUnderTheBotsSchemaName(string? schemaNameReturned, string attachedSchemaName)
    {
        _cloud.SchemaNameReturns = schemaNameReturned;
        var authored = await _workspace.ReadStringAsync(WorkspaceSynchronizer.SettingsPath, CancellationToken.None);

        var result = await AttachAsync();

        Assert.False(_baseline!.UpdateWorkspaceDirectory);
        Assert.Equal(attachedSchemaName, result.SchemaName);
        Assert.Equal(authored.Replace(SchemaName, attachedSchemaName, StringComparison.Ordinal), await _workspace.ReadStringAsync(WorkspaceSynchronizer.SettingsPath, CancellationToken.None));
    }

    [Fact]
    public async Task AttachAsync_ReadsTheTargetInOrderThroughTheClientItWasGiven()
    {
        await AttachAsync();

        Assert.Equal(new[] { nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync), nameof(ISyncDataverseClient.CreateNewAgentAsync), nameof(ISyncDataverseClient.GetSolutionVersionsAsync) }, _cloud.CallsTo(nameof(ISyncDataverseClient.SetDataverseUrl), nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync), nameof(ISyncDataverseClient.CreateNewAgentAsync), nameof(ISyncDataverseClient.GetSolutionVersionsAsync)));
    }

    [Fact]
    public async Task AttachAsync_SavesTheSyncInfoTheBaselineAndTheOperationContextWereBuiltFor()
    {
        var result = await AttachAsync();

        Assert.Same(result.SyncInfo, _saved);
        Assert.Same(result.SyncInfo, _baseline!.SyncInfo);
        Assert.Same(result.SyncInfo, _contextRequestedFor);
        Assert.Equal(FakeDataverseCloud.CreatedAgentId, result.SyncInfo.AgentId);
        Assert.Equal(DataverseEndpoint, result.SyncInfo.DataverseEndpoint);
        Assert.Equal("env-1", result.SyncInfo.EnvironmentId);
        Assert.Equal(TenantId, result.SyncInfo.AccountInfo!.TenantId);
        Assert.NotNull(result.SyncInfo.SolutionVersions);
    }

    [Fact]
    public async Task AttachAsync_WritesTheBaselineFromTheServiceWithoutReconcilingConnectorsWorkflowsOrPrompts()
    {
        _promptMetadata = ImmutableArray.Create(new AIPromptMetadata { AIModelId = Guid.NewGuid(), Name = "Summarize" });

        await AttachAsync();

        Assert.Null(_baseline!.ChangeToken);
        Assert.False(_baseline.SyncCustomConnectors);
        Assert.False(_baseline.SyncWorkflowsAndPrompts);
        Assert.Equal(_promptMetadata, _baseline.Prompts);
    }

    [Fact]
    public async Task AttachAsync_NeverWritesTheCloudCacheItself()
    {
        await AttachAsync();

        Assert.False(_workspace.Exists(CloudCachePath));
        Assert.False(_workspace.Exists(ChangeTokenPath));
        Assert.False(_workspace.Exists(SyncInfoPath));
    }

    [Fact]
    public async Task AttachAsync_ReturnsWhatTheTargetReportedForWorkflowsAndPromptsAndStillWritesTheBaseline()
    {
        _workflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = "this is not a valid connection" });
        _workflowResponses.Add(new WorkflowResponse { WorkflowName = "Notify", IsDisabled = true });
        _promptResponses.Add(new AIPromptResponse { PromptName = "Summarize", ErrorMessage = "invalid model" });

        var result = await AttachAsync();

        Assert.Equal(_workflowResponses, result.WorkflowResponses);
        Assert.Equal(_promptResponses, result.PromptResponses);
        Assert.NotNull(_baseline);
        Assert.NotNull(_saved);
    }

    [Fact]
    public async Task AttachAsync_WhenTheBaselineFails_ReportsTheCreatedAgent()
    {
        _baselineThrows = new InvalidOperationException("service unavailable");

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.Equal(WorkspaceAttachFailure.ReattachFailed, failure.Failure);
        Assert.Equal(FakeDataverseCloud.CreatedAgentId, failure.AgentId);
        Assert.Same(_baselineThrows, failure.InnerException);
        Assert.Contains(FakeDataverseCloud.CreatedAgentId.ToString(), failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(_saved);
    }

    [Fact]
    public async Task AttachAsync_WhenTheBaselineFails_RemovesThePartialCloudCacheAndKeepsTheScaffold()
    {
        await WriteAsync(_workspace, CloudCachePath.ToString(), "{}");
        _baselineThrows = new InvalidOperationException("service unavailable");

        await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.False(_workspace.Exists(CloudCachePath));
        Assert.All(_scaffolded, path => Assert.True(_workspace.Exists(new AgentFilePath(path))));
    }

    [Fact]
    public async Task AttachAsync_WhenCancelled_RethrowsTheCancellationAfterRemovingThePartialCloudCache()
    {
        await WriteAsync(_workspace, CloudCachePath.ToString(), "{}");
        _baselineThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => AttachAsync());

        Assert.False(_workspace.Exists(CloudCachePath));
        Assert.All(_scaffolded, path => Assert.True(_workspace.Exists(new AgentFilePath(path))));
    }

    [Fact]
    public async Task AttachAsync_WhenCreationFailsButTheAgentNowExists_ReportsThatAgent()
    {
        _cloud.CreateThrows = new InvalidOperationException("the response was lost");
        _cloud.SchemaProbeReturns = ExistingAgentId;

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.Equal(WorkspaceAttachFailure.ReattachFailed, failure.Failure);
        Assert.Equal(ExistingAgentId, failure.AgentId);
        Assert.Same(_cloud.CreateThrows, failure.InnerException);
    }

    [Fact]
    public async Task AttachAsync_WhenCreationFailsAndNoAgentExists_SaysNothingWasLeftBehind()
    {
        _cloud.CreateThrows = new InvalidOperationException("quota exceeded");

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.Equal(WorkspaceAttachFailure.AttachFailed, failure.Failure);
        Assert.Null(failure.AgentId);
        Assert.Same(_cloud.CreateThrows, failure.InnerException);
    }

    [Fact]
    public async Task AttachAsync_WhenCreationFailsAndTheTargetCannotBeReRead_SaysTheOutcomeIsUncertain()
    {
        _cloud.CreateThrows = new InvalidOperationException("the response was lost");
        _cloud.SchemaProbeThrows = new InvalidOperationException("the service is unavailable");

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.Equal(WorkspaceAttachFailure.AttachUncertain, failure.Failure);
        Assert.Null(failure.AgentId);
        Assert.Same(_cloud.CreateThrows, failure.InnerException);
    }

    [Fact]
    public async Task AttachAsync_WhenTheSolutionVersionsCannotBeRead_ReportsTheCreatedAgentWithoutAnotherProbe()
    {
        await WriteAsync(_workspace, CloudCachePath.ToString(), "{}");
        _cloud.SolutionVersionsThrows = new InvalidOperationException("service unavailable");

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.Equal(WorkspaceAttachFailure.ReattachFailed, failure.Failure);
        Assert.Equal(FakeDataverseCloud.CreatedAgentId, failure.AgentId);
        Assert.Single(_cloud.CallsTo(nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync)));
        Assert.False(_workspace.Exists(CloudCachePath));
    }

    [Fact]
    public async Task AttachAsync_WhenTheWorkspaceIsConnectedAndRetargetIsNotAllowed_RefusesBeforeAnyChange()
    {
        _connected = true;

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.Equal(WorkspaceAttachFailure.AlreadyConnected, failure.Failure);
        Assert.Empty(_cloud.Client.Invocations);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task AttachAsync_RetargetingWithABackup_PersistsItBeforeTheFirstCloudWriteAndFinalizesItAfterTheBinding()
    {
        _connected = true;

        Assert.True((await AttachAsync(Request(allowRetarget: true, persistRetargetBackup: true))).Retargeted);
        Assert.Equal(
            new[]
            {
                nameof(IWorkspaceSynchronizer.ReadWorkspaceDefinitionAsync),
                nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync),
                nameof(IWorkspaceRetargetService.ResetRemoteBindingState),
                nameof(IWorkspaceRetargetService.PersistRetargetBackup),
                nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync),
                nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync),
                nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync),
                nameof(IWorkspaceRetargetService.SyncWorkspaceAsync),
                nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync),
                nameof(IWorkspaceRetargetService.FinalizeRetarget),
            },
            _calls);
    }

    [Fact]
    public async Task AttachAsync_RetargetingWithoutABackup_NeverWritesOrFinalizesOne()
    {
        _connected = true;

        Assert.True((await AttachAsync(Request(allowRetarget: true))).Retargeted);
        Assert.Contains(nameof(IWorkspaceRetargetService.ResetRemoteBindingState), _calls);
        Assert.DoesNotContain(nameof(IWorkspaceRetargetService.PersistRetargetBackup), _calls);
        Assert.DoesNotContain(nameof(IWorkspaceRetargetService.FinalizeRetarget), _calls);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ClearComponentSyncBaselines), _calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttachAsync_WhenARetargetFails_RestoresThePreviousBindingAndKeepsTheCloudCache(bool persistRetargetBackup)
    {
        _connected = true;
        await WriteAsync(_workspace, CloudCachePath.ToString(), "{}");
        _baselineThrows = new InvalidOperationException("service unavailable");

        Assert.Equal(WorkspaceAttachFailure.ReattachFailed, (await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync(Request(allowRetarget: true, persistRetargetBackup: persistRetargetBackup)))).Failure);
        Assert.Contains(nameof(IWorkspaceRetargetService.RestoreRemoteBindingState), _calls);
        Assert.Equal(persistRetargetBackup, _calls.Contains(nameof(IWorkspaceRetargetService.ClearRetargetBackup)));
        Assert.DoesNotContain(nameof(IWorkspaceRetargetService.FinalizeRetarget), _calls);
        Assert.True(_workspace.Exists(CloudCachePath));
    }

    [Fact]
    public async Task AttachAsync_WhenARetargetFailsBeforeTheReset_LeavesTheConnectedWorkspaceAlone()
    {
        _connected = true;
        await WriteAsync(_workspace, CloudCachePath.ToString(), "{}");
        _cloud.SolutionVersionsThrows = new InvalidOperationException("service unavailable");

        Assert.Equal(WorkspaceAttachFailure.ReattachFailed, (await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync(Request(allowRetarget: true, persistRetargetBackup: true)))).Failure);
        Assert.DoesNotContain(nameof(IWorkspaceRetargetService.ResetRemoteBindingState), _calls);
        Assert.DoesNotContain(nameof(IWorkspaceRetargetService.RestoreRemoteBindingState), _calls);
        Assert.True(_workspace.Exists(CloudCachePath));
    }

    [Theory]
    [InlineData(null, null, true, false)]
    [InlineData("cccccccc-dddd-eeee-ffff-000000000000", null, false, true)]
    [InlineData("cccccccc-dddd-eeee-ffff-000000000000", "cccccccc-dddd-eeee-ffff-000000000000", false, false)]
    [InlineData(null, "cccccccc-dddd-eeee-ffff-000000000000", true, false)]
    public async Task AttachAsync_AdoptsTheAgentCarryingTheSchemaNameOrCreatesOne(string? existingAgentId, string? expectedAgentId, bool created, bool adopted)
    {
        _cloud.ExistingAgentId = existingAgentId is null ? Guid.Empty : Guid.Parse(existingAgentId);

        var result = await AttachAsync(Request(expectedAgentId: expectedAgentId is null ? null : Guid.Parse(expectedAgentId)));

        Assert.Equal(created ? FakeDataverseCloud.CreatedAgentId : ExistingAgentId, result.AgentId);
        Assert.Equal(created, result.AgentCreated);
        Assert.Equal(adopted, result.AdoptedExistingAgent);
        Assert.Equal(created ? 1 : 0, _cloud.CallsTo(nameof(ISyncDataverseClient.CreateNewAgentAsync)).Count);
        Assert.False(_baseline!.UpdateWorkspaceDirectory);
    }

    [Fact]
    public async Task AttachAsync_WhenAnotherAgentCarriesTheSchemaName_RefusesBeforeAnyChange()
    {
        await WriteAsync(_workspace, CloudCachePath.ToString(), "{}");
        _cloud.ExistingAgentId = ExistingAgentId;

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync(Request(expectedAgentId: Guid.NewGuid())));

        Assert.Equal(WorkspaceAttachFailure.AgentAlreadyExists, failure.Failure);
        Assert.Equal(ExistingAgentId, failure.AgentId);
        Assert.Equal(new[] { nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync) }, _cloud.Client.Invocations.Select(invocation => invocation.Method.Name));
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ClearComponentSyncBaselines), _calls);
        Assert.True(_workspace.Exists(CloudCachePath));
    }

    [Fact]
    public async Task AttachAsync_WhenTheTargetHoldsNoAgentAndCreatingIsNotAllowed_RefusesBeforeAnyChange()
    {
        Assert.Equal(WorkspaceAttachFailure.AgentNotFound, (await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync(Request(createIfMissing: false)))).Failure);
        Assert.Equal(new[] { nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync) }, _cloud.Client.Invocations.Select(invocation => invocation.Method.Name));
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ClearComponentSyncBaselines), _calls);
        Assert.Null(_baseline);
    }

    [Fact]
    public async Task AttachAsync_CreatingTheAgentUnderADisplayNameLongerThanTheLimit_RefusesBeforeCreatingIt()
    {
        var displayName = new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength + 1);

        var failure = await Assert.ThrowsAsync<AgentScaffoldException>(() => AttachAsync(Request(displayName: displayName)));

        Assert.Equal((AgentScaffoldFailure.DisplayNameTooLong, displayName), (failure.Failure, failure.Subject));
        Assert.Equal(new[] { nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync) }, _cloud.Client.Invocations.Select(invocation => invocation.Method.Name));
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ClearComponentSyncBaselines), _calls);
        Assert.Null(_baseline);
    }

    [Fact]
    public async Task AttachAsync_CreatesTheAgentUnderADisplayNameOfTheLimit()
    {
        var displayName = new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength);

        Assert.True((await AttachAsync(Request(displayName: displayName))).AgentCreated);
        _cloud.Client.Verify(client => client.CreateNewAgentAsync(displayName, SchemaName, AuthoringShape.CliCopilot, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AttachAsync_AdoptingTheAgentThatCarriesTheSchemaName_LeavesTheDisplayNameItWouldBeCreatedWithUnchecked()
    {
        _cloud.ExistingAgentId = ExistingAgentId;

        Assert.True((await AttachAsync(Request(displayName: new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength + 1)))).AdoptedExistingAgent);
        Assert.Empty(_cloud.CallsTo(nameof(ISyncDataverseClient.CreateNewAgentAsync)));
    }

    [Fact]
    public async Task AttachAsync_WhenTheWorkspaceCannotBeReattached_RefusesBeforeAnyDataverseCall()
    {
        RemoveScaffold();

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => AttachAsync());

        Assert.Equal(WorkspaceAttachFailure.ReattachNotSupported, failure.Failure);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
        Assert.Empty(_cloud.Client.Invocations);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync), _calls);
    }

    [Fact]
    public async Task AttachAsync_WithoutARequestOrAClient_Throws()
    {
        var service = CreateService();

        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => service.AttachAsync(null!, _cloud.Client.Object, CancellationToken.None))).ParamName);
        Assert.Equal("dataverseClient", (await Assert.ThrowsAsync<ArgumentNullException>(() => service.AttachAsync(Request(), null!, CancellationToken.None))).ParamName);
    }

    [Theory]
    [InlineData(false, SchemaName)]
    [InlineData(true, " ")]
    public async Task AttachAsync_WithoutATargetEndpointOrSchemaName_RefusesBeforeTouchingAnything(bool namesTheEndpoint, string schemaName)
    {
        var request = new WorkspaceAttachRequest { WorkspaceFolder = Workspace, DataverseEndpoint = namesTheEndpoint ? DataverseEndpoint : null!, EnvironmentId = "env-1", DisplayName = "Contoso Helpdesk", SchemaName = schemaName };

        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentException>(() => AttachAsync(request))).ParamName);
        Assert.Empty(_cloud.Client.Invocations);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Constructor_WithoutADependency_Throws()
    {
        Assert.Equal("synchronizer", Assert.Throws<ArgumentNullException>(() => new WorkspaceAttachService(null!, _retarget.Object, _cacheWriter.Object, _contexts.Object, _files, TimeProvider.System)).ParamName);
        Assert.Equal("retargetService", Assert.Throws<ArgumentNullException>(() => new WorkspaceAttachService(_synchronizer.Object, null!, _cacheWriter.Object, _contexts.Object, _files, TimeProvider.System)).ParamName);
        Assert.Equal("cloudCacheWriter", Assert.Throws<ArgumentNullException>(() => new WorkspaceAttachService(_synchronizer.Object, _retarget.Object, null!, _contexts.Object, _files, TimeProvider.System)).ParamName);
        Assert.Equal("operationContexts", Assert.Throws<ArgumentNullException>(() => new WorkspaceAttachService(_synchronizer.Object, _retarget.Object, _cacheWriter.Object, null!, _files, TimeProvider.System)).ParamName);
        Assert.Equal("fileAccessorFactory", Assert.Throws<ArgumentNullException>(() => new WorkspaceAttachService(_synchronizer.Object, _retarget.Object, _cacheWriter.Object, _contexts.Object, null!, TimeProvider.System)).ParamName);
        Assert.Equal("timeProvider", Assert.Throws<ArgumentNullException>(() => new WorkspaceAttachService(_synchronizer.Object, _retarget.Object, _cacheWriter.Object, _contexts.Object, _files, null!)).ParamName);
    }

    [Fact]
    public async Task AttachAsync_CreatingThroughTheSynchronizer_LeavesTheCloudCacheEqualToAFreshClone()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));

        var outcome = await AttachInMemoryAsync(factory, session.Root, retarget: false);

        Assert.True(outcome.Result.AgentCreated);
        Assert.Equal(AuthoringShape.CliCopilot, outcome.Cloud.LastAuthoringShape);
        Assert.Equal(0, outcome.Cloud.Workflows[WorkflowId].StateCode);
        Assert.Equal(FakeDataverseCloud.CreatedAgentId, outcome.SavedSyncInfo.AgentId);
        Assert.Contains(WeatherReference, outcome.Cache, StringComparison.Ordinal);
        Assert.Contains(WorkflowId.ToString(), outcome.Cache, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(outcome.FreshCache, outcome.Cache);
    }

    [Fact]
    public async Task AttachAsync_RetargetingThroughTheSynchronizerInMemory_LeavesTheCloudCacheEqualToAFreshCloneAndNoBackup()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseWorkspace(new DirectoryPath("operation/"));

        var outcome = await AttachInMemoryAsync(factory, session.Root, retarget: true);

        Assert.True(outcome.Result.Retargeted);
        Assert.True(outcome.Result.AdoptedExistingAgent);
        Assert.Equal(ExistingAgentId, outcome.SavedSyncInfo.AgentId);
        Assert.False(outcome.RetargetBackupExists);
        Assert.Equal(0, outcome.Cloud.Workflows[WorkflowId].StateCode);
        Assert.Contains("cree9_agent.topic.Target", outcome.Cache, StringComparison.Ordinal);
        Assert.DoesNotContain("cree9_agent.topic.Source", outcome.Cache, StringComparison.Ordinal);
        Assert.Contains(WeatherReference, outcome.Cache, StringComparison.Ordinal);
        Assert.Contains(WorkflowId.ToString(), outcome.Cache, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(outcome.FreshCache, outcome.Cache);
    }

    [Fact]
    public async Task ConnectAsync_WritesEachFolderCacheFromTheTargetBeforeSavingItsSyncInfo()
    {
        var agentId = await SeedExistingAgentAsync();

        var result = await ConnectAsync();
        var agent = _cacheWrites[0].SyncInfo;
        var collection = _cacheWrites[1].SyncInfo;

        Assert.Equal(agentId, result.AgentId);
        Assert.Empty(result.SkippedCollections);
        Assert.Equal((agentId, (Guid?)null, DataverseEndpoint, "env-1", TenantId), (agent.AgentId!.Value, agent.ComponentCollectionId, agent.DataverseEndpoint, agent.EnvironmentId, agent.AccountInfo!.TenantId));
        Assert.Equal((_environment.CollectionIdOf(ToolsSchemaName), (Guid?)null, DataverseEndpoint, "env-1", TenantId), (collection.ComponentCollectionId!.Value, collection.AgentId, collection.DataverseEndpoint, collection.EnvironmentId, collection.AccountInfo!.TenantId));
        Assert.Equal(new[] { $"{nameof(IWorkspaceCloudCacheWriter.WriteCloudCacheAsync)} {Workspace}", nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), $"{nameof(IWorkspaceCloudCacheWriter.WriteCloudCacheAsync)} {ToolsFolder}", nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync) }, _calls);
        Assert.Equal(new[] { Workspace, ToolsFolder }, _cacheWrites.Select(write => write.Folder));
        _synchronizer.Verify(synchronizer => synchronizer.SaveSyncInfoAsync(Workspace, agent), Times.Once);
        _synchronizer.Verify(synchronizer => synchronizer.SaveSyncInfoAsync(ToolsFolder, collection), Times.Once);
        Assert.All(_environment.Cloud.Client.Invocations, invocation => Assert.Contains(invocation.Method.Name, CloudReads));
    }

    [Fact]
    public async Task ConnectAsync_WhenTheTargetHoldsNoSuchAgent_RefusesBeforeWritingAnything()
    {
        await SeedExistingAgentAsync();
        var content = await FilesOfAsync(_files, Workspace, ToolsFolder);

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => ConnectAsync(ConnectRequest(schemaName: "con_missing")));

        Assert.Equal(WorkspaceAttachFailure.AgentNotFound, failure.Failure);
        Assert.Null(failure.AgentId);
        Assert.Contains("'con_missing'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("env-1", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
        Assert.Empty(_cacheWrites);
        Assert.Empty(_calls);
        Assert.Equal(content, await FilesOfAsync(_files, Workspace, ToolsFolder));
        Assert.All(_environment.Cloud.Client.Invocations, invocation => Assert.Contains(invocation.Method.Name, CloudReads));
    }

    [Fact]
    public async Task ConnectAsync_WhenTheEnvironmentHoldsNotEveryCollection_ConnectsTheOthersAndNamesTheRestInRequestOrder()
    {
        var agentId = await SeedExistingAgentAsync();
        var zetaFolder = new DirectoryPath("operation/Zeta Tools/");
        var alphaFolder = new DirectoryPath("operation/Alpha Tools/");
        await WriteAsync(_files.Create(zetaFolder), "collection.mcs.yml", "schemaName: cree9_zeta\n");
        await BindAsync(alphaFolder, Guid.NewGuid(), environmentId: "env-2");
        var skipped = await FilesOfAsync(_files, zetaFolder, alphaFolder);

        var result = await ConnectAsync(ConnectRequest(collectionFolders: new Dictionary<string, DirectoryPath> { ["cree9_zeta"] = zetaFolder, [ToolsSchemaName.ToUpperInvariant()] = ToolsFolder, ["cree9_alpha"] = alphaFolder }));

        Assert.Equal(agentId, result.AgentId);
        Assert.Equal(new[] { "cree9_zeta", "cree9_alpha" }, result.SkippedCollections);
        Assert.Equal(new[] { (Workspace, (Guid?)null), (ToolsFolder, (Guid?)_environment.CollectionIdOf(ToolsSchemaName)) }, _cacheWrites.Select(write => (write.Folder, write.SyncInfo.ComponentCollectionId)));
        Assert.Equal(skipped, await FilesOfAsync(_files, zetaFolder, alphaFolder));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConnectAsync_WhenFoldersAreConnectedToTheSameAgentAndCollection_RefreshesThem(bool workspaceConnected, bool toolsConnected)
    {
        var agentId = await SeedExistingAgentAsync();

        if (workspaceConnected)
        {
            await BindAsync(Workspace, agentId, environmentId: "ENV-1");
        }

        if (toolsConnected)
        {
            await BindAsync(ToolsFolder, null, _environment.CollectionIdOf(ToolsSchemaName));
        }

        await ConnectAsync();

        Assert.Equal(new[] { $"{nameof(IWorkspaceCloudCacheWriter.WriteCloudCacheAsync)} {Workspace}", nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync), $"{nameof(IWorkspaceCloudCacheWriter.WriteCloudCacheAsync)} {ToolsFolder}", nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync) }, _calls);
        Assert.All(_environment.Cloud.Client.Invocations, invocation => Assert.Contains(invocation.Method.Name, CloudReads));
    }

    [Theory]
    [InlineData(true, "another agent")]
    [InlineData(true, "a component collection")]
    [InlineData(true, "another environment")]
    [InlineData(false, "the agent")]
    [InlineData(false, "another component collection")]
    [InlineData(false, "another environment")]
    public async Task ConnectAsync_WhenAFolderIsConnectedElsewhere_RefusesNamingItBeforeWritingAnything(bool workspace, string elsewhere)
    {
        var agentId = await SeedExistingAgentAsync();
        var toolsId = _environment.CollectionIdOf(ToolsSchemaName);
        var folder = workspace ? Workspace : ToolsFolder;
        await (elsewhere switch
        {
            "another agent" => BindAsync(folder, Guid.NewGuid()),
            "a component collection" => BindAsync(folder, null, toolsId),
            "the agent" => BindAsync(folder, agentId),
            "another component collection" => BindAsync(folder, null, Guid.NewGuid()),
            _ => workspace ? BindAsync(folder, agentId, environmentId: "env-2") : BindAsync(folder, null, toolsId, "env-2"),
        });
        var content = await FilesOfAsync(_files, Workspace, ToolsFolder);

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => ConnectAsync());

        Assert.Equal(WorkspaceAttachFailure.ConnectBoundToAnotherAgent, failure.Failure);
        Assert.Equal(agentId, failure.AgentId);
        Assert.Contains($"'{folder}'", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_cacheWrites);
        Assert.Empty(_calls);
        Assert.Equal(content, await FilesOfAsync(_files, Workspace, ToolsFolder));
        Assert.All(_environment.Cloud.Client.Invocations, invocation => Assert.Contains(invocation.Method.Name, CloudReads));
    }

    [Fact]
    public async Task ConnectAsync_WhenSomeFoldersAreConnectedElsewhere_NamesEachOfThemAndRefreshesNone()
    {
        var agentId = await SeedExistingAgentAsync();
        var moreFolder = new DirectoryPath("operation/More Tools/");
        var moreId = _environment.SeedCollection("cree9_more", "More Tools");
        await BindAsync(Workspace, agentId);
        await BindAsync(ToolsFolder, null, Guid.NewGuid());
        await BindAsync(moreFolder, null, moreId, "env-2");
        var content = await FilesOfAsync(_files, Workspace, ToolsFolder, moreFolder);

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => ConnectAsync(ConnectRequest(collectionFolders: new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = ToolsFolder, ["cree9_more"] = moreFolder })));

        Assert.Equal(WorkspaceAttachFailure.ConnectBoundToAnotherAgent, failure.Failure);
        Assert.Contains($"'{ToolsFolder}', '{moreFolder}'.", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain($"'{Workspace}'", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_cacheWrites);
        Assert.Equal(content, await FilesOfAsync(_files, Workspace, ToolsFolder, moreFolder));
    }

    [Fact]
    public async Task ConnectAsync_WhenACacheCannotBeWritten_LeavesEveryCloudCacheAsItWasAndRethrows()
    {
        var agentId = await SeedExistingAgentAsync();
        await BindAsync(Workspace, agentId);
        await WriteAsync(_workspace, CloudCachePath.ToString(), "\uFEFF{\r\n\"stale\": true\r\n}");
        await WriteAsync(_workspace, ".mcs/workflow-state.json", "{}");
        var content = await FilesOfAsync(_files, Workspace, ToolsFolder);
        var unreachable = new HttpRequestException("The island could not be reached.");
        _cacheWritten = folder => folder == ToolsFolder ? Task.FromException(unreachable) : Task.CompletedTask;

        Assert.Same(unreachable, await Assert.ThrowsAsync<HttpRequestException>(() => ConnectAsync()));
        Assert.Equal(new[] { Workspace, ToolsFolder }, _cacheWrites.Select(write => write.Folder));
        Assert.Equal(content, await FilesOfAsync(_files, Workspace, ToolsFolder));
    }

    [Fact]
    public async Task ConnectAsync_WhenCancelledBetweenFolders_LeavesEveryCloudCacheAsItWasAndRethrowsTheCancellation()
    {
        await SeedExistingAgentAsync();
        var content = await FilesOfAsync(_files, Workspace, ToolsFolder);
        using var cancellation = new CancellationTokenSource();
        _cacheWritten = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectAsync(cancellationToken: cancellation.Token));

        Assert.Equal(new[] { $"{nameof(IWorkspaceCloudCacheWriter.WriteCloudCacheAsync)} {Workspace}", nameof(IWorkspaceSynchronizer.SaveSyncInfoAsync) }, _calls);
        Assert.Equal(content, await FilesOfAsync(_files, Workspace, ToolsFolder));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConnectAsync_WhenAFolderCannotBeRestored_RestoresTheOthersAndRethrowsTheOriginalFailure(bool connected, bool denied)
    {
        var agentId = await SeedExistingAgentAsync();

        if (connected)
        {
            await BindAsync(Workspace, agentId);
        }

        var tools = await FilesOfAsync(_files, ToolsFolder);
        var unreachable = new HttpRequestException("The island could not be reached.");
        Exception restoreFailure = denied ? new UnauthorizedAccessException("The .mcs folder is read-only.") : new IOException("The .mcs folder is in use.");
        _cacheWritten = folder => folder == ToolsFolder ? Task.FromException(unreachable) : Task.CompletedTask;
        _retarget.Setup(retarget => retarget.RestoreRemoteBindingState(Workspace, _snapshot)).Throws(restoreFailure);
        var workspace = new Mock<IFileAccessor>(MockBehavior.Strict);
        workspace.Setup(accessor => accessor.ListFiles(It.IsAny<string?>(), It.IsAny<string>())).Returns<string?, string>((folder, pattern) => _workspace.ListFiles(folder, pattern));
        workspace.Setup(accessor => accessor.DeleteDirectory(It.IsAny<AgentFilePath>())).Throws(restoreFailure);
        var files = new Mock<IFileAccessorFactory>(MockBehavior.Strict);
        files.Setup(factory => factory.Create(It.IsAny<DirectoryPath>())).Returns<DirectoryPath>(folder => folder == Workspace ? workspace.Object : _files.Create(folder));
        var service = new WorkspaceAttachService(_synchronizer.Object, _retarget.Object, _cacheWriter.Object, _contexts.Object, files.Object, TimeProvider.System);

        Assert.Same(unreachable, await Assert.ThrowsAsync<HttpRequestException>(() => service.ConnectAsync(ConnectRequest(), _environment.Cloud.Client.Object, CancellationToken.None)));
        Assert.True(_workspace.Exists(CloudCachePath));
        Assert.Equal(tools, await FilesOfAsync(_files, ToolsFolder));
    }

    [Fact]
    public async Task ConnectAsync_WithACollectionAndAClientThatCannotQueryCollections_ThrowsBeforeWritingAnything()
    {
        _cloud.ExistingAgentId = ExistingAgentId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().ConnectAsync(ConnectRequest(), _cloud.Client.Object, CancellationToken.None));

        Assert.Empty(_cacheWrites);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task ConnectAsync_WithoutARequestOrAClient_Throws()
    {
        var service = CreateService();

        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => service.ConnectAsync(null!, _environment.Cloud.Client.Object, CancellationToken.None))).ParamName);
        Assert.Equal("dataverseClient", (await Assert.ThrowsAsync<ArgumentNullException>(() => service.ConnectAsync(ConnectRequest(), null!, CancellationToken.None))).ParamName);
    }

    public static TheoryData<WorkspaceConnectRequest> IncompleteConnectRequests() => new()
    {
        ConnectRequest(namesTheEndpoint: false),
        ConnectRequest(environmentId: " "),
        ConnectRequest(schemaName: " "),
        new WorkspaceConnectRequest { WorkspaceFolder = Workspace, DataverseEndpoint = DataverseEndpoint, EnvironmentId = "env-1", SchemaName = SchemaName, CollectionFolders = null! },
        ConnectRequest(collectionFolders: new Dictionary<string, DirectoryPath> { [" "] = ToolsFolder }),
        ConnectRequest(collectionFolders: new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = ToolsFolder, [ToolsSchemaName.ToUpperInvariant()] = new("operation/Tools Again/") }),
        ConnectRequest(collectionFolders: new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = Workspace }),
        ConnectRequest(collectionFolders: new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = ToolsFolder, ["cree9_zeta"] = ToolsFolder }),
    };

    [Theory]
    [MemberData(nameof(IncompleteConnectRequests))]
    public async Task ConnectAsync_WithAnIncompleteRequest_RefusesBeforeTouchingAnything(WorkspaceConnectRequest request)
    {
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentException>(() => ConnectAsync(request))).ParamName);
        Assert.Empty(_environment.Cloud.Client.Invocations);
        Assert.Empty(_cacheWrites);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task ConnectAsync_ThroughTheSynchronizer_WritesTheCloudCacheOfAFreshCloneFromTheTargetAloneWithoutWritingToItOrTouchingTheContent()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        await WriteAsync(factory.Create(clone.AgentFolder), "topics/LocalOnly.mcs.yml", AgentWorkspaceHostPushTests.Topic("LocalOnly"));
        await WriteAsync(factory.Create(clone.AgentFolder), $"{WorkflowWorkspace.RelativeFolderFor("Local", Guid.NewGuid())}/{WorkflowWorkspace.DefinitionFileName}", WeatherDefinition);
        await WriteAsync(factory.Create(clone.ToolsFolder), "topics/LocalLookup.mcs.yml", AgentWorkspaceHostPushTests.Topic("LocalLookup"));
        var content = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);

        var result = await clone.Service.ConnectAsync(clone.Request, clone.Environment.Cloud.Client.Object, CancellationToken.None);
        var connected = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);

        Assert.Equal(clone.AgentId, result.AgentId);
        Assert.Equal(clone.ToolsId, (await clone.Synchronizer.GetSyncInfoAsync(clone.ToolsFolder)).ComponentCollectionId);
        Assert.Equal(content, connected.Where(file => !IsCloudCache(file.Key)));
        Assert.Equal(clone.ClonedCache.Where(file => !file.Key.EndsWith("/.connectors-download.json", StringComparison.Ordinal)), connected.Where(file => IsCloudCache(file.Key)));
        Assert.Equal("*", await factory.Create(clone.AgentFolder).ReadStringAsync(new AgentFilePath(".mcs/.gitignore"), CancellationToken.None));
        Assert.Contains(WorkflowId.ToString(), connected[$"{clone.AgentFolder}{CloudCachePath}"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WeatherReference, connected[$"{clone.AgentFolder}{CloudCachePath}"], StringComparison.Ordinal);
        Assert.Contains("Summarize", connected[$"{clone.AgentFolder}{CloudCachePath}"], StringComparison.Ordinal);
        Assert.Contains(clone.Environment.Cloud.Client.Invocations, invocation => invocation.Method.Name == nameof(ISyncDataverseClient.DownloadAllWorkflowsForAgentAsync));
        Assert.All(clone.Environment.Cloud.Client.Invocations, invocation => Assert.Contains(invocation.Method.Name, CloudReads));
        Assert.Empty(clone.Environment.Island.Saves);
    }

    [Theory]
    [InlineData(nameof(ISyncDataverseClient.DownloadAllWorkflowsForAgentAsync))]
    [InlineData(nameof(ISyncDataverseClient.DownloadAllAIPromptsForAgentAsync))]
    [InlineData(nameof(IIslandControlPlaneService.GetComponentsAsync))]
    public async Task ConnectAsync_ThroughTheSynchronizer_WhenTheTargetCannotBeRead_RemovesEveryCacheAndRethrows(string failingRead)
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        var content = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);
        var unreachable = new HttpRequestException("The target could not be reached.");
        FailRead(clone, failingRead, unreachable);

        Assert.Same(unreachable, await Assert.ThrowsAsync<HttpRequestException>(() => clone.Service.ConnectAsync(clone.Request, clone.Environment.Cloud.Client.Object, CancellationToken.None)));
        Assert.Equal(content, await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder));
    }

    [Fact]
    public async Task ConnectAsync_ThroughTheSynchronizer_RefreshesAConnectedFolderToAFreshConnectKeepingTheFilesSyncDoesNotOwn()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await ConnectedCloneAsync(factory, session.Root);
        var fresh = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);
        await MakeStaleAsync(factory.Create(clone.AgentFolder));

        await clone.Service.ConnectAsync(clone.Request, clone.Environment.Cloud.Client.Object, CancellationToken.None);

        Assert.Equal(fresh.Append(new KeyValuePair<string, string>($"{clone.AgentFolder}.mcs/workflow-state.json", "\uFEFF{\r\n}")).OrderBy(file => file.Key, StringComparer.Ordinal), await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder));
        Assert.All(clone.Environment.Cloud.Client.Invocations, invocation => Assert.Contains(invocation.Method.Name, CloudReads));
        Assert.Empty(clone.Environment.Island.Saves);
    }

    [Theory]
    [InlineData(nameof(ISyncDataverseClient.DownloadAllWorkflowsForAgentAsync))]
    [InlineData(nameof(IIslandControlPlaneService.GetComponentsAsync))]
    public async Task ConnectAsync_ThroughTheSynchronizer_WhenARefreshCannotReadTheTarget_RestoresEveryFolderByteForByte(string failingRead)
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await ConnectedCloneAsync(factory, session.Root);
        await MakeStaleAsync(factory.Create(clone.AgentFolder));
        var stale = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);
        var unreachable = new HttpRequestException("The target could not be reached.");
        FailRead(clone, failingRead, unreachable);

        Assert.Same(unreachable, await Assert.ThrowsAsync<HttpRequestException>(() => clone.Service.ConnectAsync(clone.Request, clone.Environment.Cloud.Client.Object, CancellationToken.None)));
        Assert.Equal(stale, await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConnectAsync_ThroughTheSynchronizerOnDisk_WhenItFails_RestoresTheBindingFilesAndLeavesTheWorkflowStateAlone(bool connected)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "mcs-connect-" + Guid.NewGuid().ToString("N"));
        var writtenOn = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            var factory = new FileAccessorFactory();
            var root = new DirectoryPath(sandbox.Replace('\\', '/') + "/");
            var clone = connected ? await ConnectedCloneAsync(factory, root) : await CloneWithoutCloudCacheAsync(factory, root);
            await (connected ? MakeStaleAsync(factory.Create(clone.AgentFolder)) : WriteAsync(factory.Create(clone.AgentFolder), ".mcs/workflow-state.json", "\uFEFF{\r\n}"));
            var cacheFolder = Path.Combine(sandbox, "Contoso Helpdesk", WorkspaceSynchronizer.HiddenRoot);
            var workflowState = Path.Combine(cacheFolder, "workflow-state.json");
            File.SetLastWriteTimeUtc(workflowState, writtenOn);
            var before = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);
            var unreachable = new HttpRequestException("The target could not be reached.");
            FailRead(clone, nameof(IIslandControlPlaneService.GetComponentsAsync), unreachable);

            Assert.Same(unreachable, await Assert.ThrowsAsync<HttpRequestException>(() => clone.Service.ConnectAsync(clone.Request, clone.Environment.Cloud.Client.Object, CancellationToken.None)));
            Assert.Equal(connected ? before : before.Append(new KeyValuePair<string, string>($"{clone.AgentFolder}.mcs/.gitignore", "\uFEFF*")).OrderBy(file => file.Key, StringComparer.Ordinal), await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder));
            Assert.Equal(writtenOn, File.GetLastWriteTimeUtc(workflowState));
            Assert.True(new DirectoryInfo(cacheFolder).Attributes.HasFlag(FileAttributes.Hidden));
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConnectAndMergeAsync_ReadsTheEnvironmentOnceWithTheCallsOfAClone()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        clone.Environment.Island.Service.Invocations.Clear();

        await clone.Service.ConnectAndMergeAsync(MergeRequest(clone, SnapshotOf(factory, clone)), clone.Environment.Cloud.Client.Object, CancellationToken.None);
        var calls = clone.Environment.DataverseCalls();
        var islandCalls = clone.Environment.Island.Service.Invocations.Select(invocation => invocation.Method.Name).ToArray();
        clone.Environment.Cloud.Client.Invocations.Clear();
        clone.Environment.Island.Service.Invocations.Clear();
        await clone.Environment.CreateHost().CloneAsync(new AgentCloneRequest(HostedCloud.Target(SchemaName), "Contoso Helpdesk", new Dictionary<string, string> { [ToolsSchemaName] = "Shared Tools" }, Array.Empty<string>()), new RecordingAuthProvider(), CancellationToken.None);

        Assert.Equal(clone.Environment.DataverseCalls(), calls);
        Assert.Equal(clone.Environment.Island.Service.Invocations.Select(invocation => invocation.Method.Name), islandCalls);
        Assert.Single(calls, nameof(ISyncDataverseClient.GetAgentInfoAsync));
        Assert.Empty(clone.Environment.Island.Saves);
    }

    [Fact]
    public async Task ConnectAndMergeAsync_WritesEachFolderTheCloudCacheOfItsReadWhichConnectAsyncWritesForTheSameCloud()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);

        var result = await clone.Service.ConnectAndMergeAsync(MergeRequest(clone, SnapshotOf(factory, clone)), clone.Environment.Cloud.Client.Object, CancellationToken.None);
        var merged = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);
        factory.Create(clone.AgentFolder).DeleteDirectory(new AgentFilePath(WorkspaceSynchronizer.HiddenRoot));
        factory.Create(clone.ToolsFolder).DeleteDirectory(new AgentFilePath(WorkspaceSynchronizer.HiddenRoot));
        await clone.Service.ConnectAsync(clone.Request, clone.Environment.Cloud.Client.Object, CancellationToken.None);
        var connected = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);

        Assert.Equal(clone.AgentId, result.AgentId);
        Assert.Empty(result.Outcomes);
        Assert.Empty(result.SkippedCollectionFolders);
        Assert.Empty(result.AddedCollectionFolders);
        Assert.Equal(clone.ClonedCache, merged.Where(file => IsCloudCache(file.Key)));
        Assert.Equal(connected.Where(file => IsCloudCache(file.Key)), merged.Where(file => IsCloudCache(file.Key) && !file.Key.EndsWith("/.connectors-download.json", StringComparison.Ordinal)));
        Assert.Equal(connected.Where(file => !IsCloudCache(file.Key)), merged.Where(file => !IsCloudCache(file.Key)));
    }

    [Fact]
    public async Task ConnectAndMergeAsync_WritesEachOutcomeOfTheMergeAndNeverMergesTheCloudCache()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        var agent = factory.Create(clone.AgentFolder);
        var baseFiles = SnapshotOf(factory, clone);
        var greeting = TextOf(baseFiles, "Contoso Helpdesk/topics/Greeting.mcs.yml");
        var settings = TextOf(baseFiles, "Contoso Helpdesk/settings.mcs.yml");
        var lookup = TextOf(baseFiles, "Shared Tools/topics/Lookup.mcs.yml");
        var references = TextOf(baseFiles, "Contoso Helpdesk/references.mcs.yml");
        var agentDefinition = TextOf(baseFiles, "Contoso Helpdesk/agent.mcs.yml");
        clone.Environment.Island.Seed(clone.AgentId, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Farewell", "Farewell"));
        await KeepInBaseAndLocallyAsync(factory.Create(clone.ToolsFolder), baseFiles, "Shared Tools", "topics/Lookup.mcs.yml", "kind: Lookup\n");
        await KeepInBaseAndLocallyAsync(agent, baseFiles, "Contoso Helpdesk", "topics/Retired.mcs.yml", "kind: AdaptiveDialog\n");
        baseFiles["Contoso Helpdesk/settings.mcs.yml"] = Encoding.UTF8.GetBytes("# head\r\n" + settings);
        await WriteAsync(agent, "settings.mcs.yml", "# head\r\n" + settings + "\r\n# local");
        baseFiles["Contoso Helpdesk/topics/Greeting.mcs.yml"] = Encoding.UTF8.GetBytes("kind: Base\n");
        await WriteAsync(agent, "topics/Greeting.mcs.yml", "kind: Local\n");
        baseFiles["Contoso Helpdesk/agent.mcs.yml"] = new byte[] { 0x00, 0x01 };
        await WriteAsync(agent, "agent.mcs.yml", "# local\n");
        baseFiles["Contoso Helpdesk/references.mcs.yml"] = Encoding.UTF8.GetBytes("# base\n");
        agent.Delete(new AgentFilePath("references.mcs.yml"));
        baseFiles["Contoso Helpdesk/topics/Obsolete.mcs.yml"] = Encoding.UTF8.GetBytes("kind: AdaptiveDialog\n");
        await WriteAsync(agent, "topics/Obsolete.mcs.yml", "kind: Obsolete\n");
        baseFiles["Contoso Helpdesk/.mcs/botdefinition.json"] = Encoding.UTF8.GetBytes("{}");

        var result = await clone.Service.ConnectAndMergeAsync(MergeRequest(clone, baseFiles), clone.Environment.Cloud.Client.Object, CancellationToken.None);
        var files = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);

        Assert.Equal(
            new[]
            {
                ("Contoso Helpdesk/agent.mcs.yml", WorkingCopyMergeOutcome.Replaced),
                ("Contoso Helpdesk/references.mcs.yml", WorkingCopyMergeOutcome.Conflicted),
                ("Contoso Helpdesk/settings.mcs.yml", WorkingCopyMergeOutcome.Merged),
                ("Contoso Helpdesk/topics/Farewell.mcs.yml", WorkingCopyMergeOutcome.Added),
                ("Contoso Helpdesk/topics/Greeting.mcs.yml", WorkingCopyMergeOutcome.Conflicted),
                ("Contoso Helpdesk/topics/Obsolete.mcs.yml", WorkingCopyMergeOutcome.Conflicted),
                ("Contoso Helpdesk/topics/Retired.mcs.yml", WorkingCopyMergeOutcome.Deleted),
                ("Shared Tools/topics/Lookup.mcs.yml", WorkingCopyMergeOutcome.Updated),
            },
            result.Outcomes.Select(outcome => (outcome.Key, outcome.Value)).OrderBy(outcome => outcome.Key, StringComparer.Ordinal));
        Assert.Equal(agentDefinition, files[$"{clone.AgentFolder}agent.mcs.yml"]);
        Assert.Equal(references, files[$"{clone.AgentFolder}references.mcs.yml"]);
        Assert.Equal(settings + "\r\n# local", files[$"{clone.AgentFolder}settings.mcs.yml"]);
        Assert.Equal(greeting, files[$"{clone.AgentFolder}topics/Farewell.mcs.yml"]);
        Assert.Equal(ComponentWriterDefensiveTests.CreateSyncInfrastructure().sync.MergeStrings("kind: Base\n", "kind: Local\n", greeting + "\n"), files[$"{clone.AgentFolder}topics/Greeting.mcs.yml"]);
        Assert.Equal("kind: Obsolete\n", files[$"{clone.AgentFolder}topics/Obsolete.mcs.yml"]);
        Assert.DoesNotContain($"{clone.AgentFolder}topics/Retired.mcs.yml", files.Keys);
        Assert.Equal(lookup, files[$"{clone.ToolsFolder}topics/Lookup.mcs.yml"]);
        Assert.Contains("Farewell", files[$"{clone.AgentFolder}{CloudCachePath}"], StringComparison.Ordinal);
        Assert.Equal(clone.AgentId, (await clone.Synchronizer.GetSyncInfoAsync(clone.AgentFolder)).AgentId);
        Assert.Equal(clone.ToolsId, (await clone.Synchronizer.GetSyncInfoAsync(clone.ToolsFolder)).ComponentCollectionId);
    }

    [Fact]
    public async Task ConnectAndMergeAsync_LeavesLocalFilesThatDifferFromTheAgentOnlyInWhatAPushAndACloneDoNotCarry()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        var agent = factory.Create(clone.AgentFolder);
        var baseFiles = SnapshotOf(factory, clone);
        var metadataPath = $"{WorkflowWorkspace.RelativeFolderFor("Weather", WorkflowId)}/{WorkflowWorkspace.MetadataFileName}";
        var activated = TextOf(baseFiles, $"Contoso Helpdesk/{metadataPath}");
        var draft = activated.Replace("stateCode: 1", "stateCode: 0", StringComparison.Ordinal).Replace("statusCode: 2", "statusCode: 1", StringComparison.Ordinal);
        await KeepInBaseAndLocallyAsync(agent, baseFiles, "Contoso Helpdesk", metadataPath, draft);
        await KeepInBaseAndLocallyAsync(agent, baseFiles, "Contoso Helpdesk", "settings.mcs.yml", "\uFEFF" + TextOf(baseFiles, "Contoso Helpdesk/settings.mcs.yml").Replace("\r\n", "\n", StringComparison.Ordinal));
        var checkedOut = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);

        var result = await clone.Service.ConnectAndMergeAsync(MergeRequest(clone, baseFiles), clone.Environment.Cloud.Client.Object, CancellationToken.None);

        Assert.NotEqual(activated, draft);
        Assert.Empty(result.Outcomes);
        Assert.Equal(checkedOut, (await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder)).Where(file => !IsCloudCache(file.Key)));
    }

    [Fact]
    public async Task ConnectAndMergeAsync_SkipsTheRequestedCollectionsTheAgentDoesNotLinkAndGivesEachLinkedOneTheRequestDoesNotNameAFreeFolder()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        var moreId = clone.Environment.SeedCollection("cree9_more", "More Tools", PushComponentCollectionExclusionTests.Topic("cree9_more.topic.Extra", "Extra"));
        clone.Environment.Link(clone.AgentId, moreId);
        var zetaFolder = session.Root.GetChildDirectoryPath("Zeta Tools/");
        var baseFiles = SnapshotOf(factory, clone);
        await KeepInBaseAndLocallyAsync(factory.Create(zetaFolder), baseFiles, "Zeta Tools", "collection.mcs.yml", "schemaName: cree9_zeta\n");
        baseFiles["Zeta Tools/topics/Gone.mcs.yml"] = Encoding.UTF8.GetBytes("kind: AdaptiveDialog\n");
        var zeta = await FilesOfAsync(factory, zetaFolder);
        var asked = new List<string>();

        var result = await clone.Service.ConnectAndMergeAsync(
            new WorkspaceConnectMergeRequest
            {
                Connect = new WorkspaceConnectRequest { WorkspaceFolder = clone.AgentFolder, DataverseEndpoint = HostedCloud.Organization, EnvironmentId = HostedCloud.EnvironmentId, TenantId = HostedCloud.TenantId, SchemaName = SchemaName, CollectionFolders = new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = clone.ToolsFolder, ["cree9_zeta"] = zetaFolder } },
                BaseFiles = baseFiles,
                OccupiedFolders = new[] { "More Tools" },
                IsFolderTaken = (folder, _) =>
                {
                    asked.Add(folder);
                    return new ValueTask<bool>(folder == "More Tools-2");
                },
            },
            clone.Environment.Cloud.Client.Object,
            CancellationToken.None);
        var moreFolder = session.Root.GetChildDirectoryPath("More Tools-3/");

        Assert.Equal(new[] { zetaFolder }, result.SkippedCollectionFolders);
        Assert.Equal(new[] { moreFolder }, result.AddedCollectionFolders);
        Assert.Equal(new[] { "More Tools-2", "More Tools-3" }, asked);
        Assert.Equal(WorkingCopyMergeOutcome.Added, result.Outcomes["More Tools-3/collection.mcs.yml"]);
        Assert.Equal(WorkingCopyMergeOutcome.Added, result.Outcomes["More Tools-3/topics/Extra.mcs.yml"]);
        Assert.DoesNotContain(result.Outcomes.Keys, path => path.StartsWith("Zeta Tools/", StringComparison.Ordinal));
        Assert.Equal(new[] { $"{moreFolder}collection.mcs.yml", $"{moreFolder}topics/Extra.mcs.yml" }, (await FilesOfAsync(factory, moreFolder)).Keys.Where(path => !IsCloudCache(path)));
        Assert.Equal(moreId, (await clone.Synchronizer.GetSyncInfoAsync(moreFolder)).ComponentCollectionId);
        Assert.Equal(zeta, await FilesOfAsync(factory, zetaFolder));
    }

    [Fact]
    public async Task ConnectAndMergeAsync_WhenAFolderIsConnectedElsewhere_RefusesBeforeWritingAnything()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        clone.Environment.Island.Seed(clone.AgentId, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Farewell", "Farewell"));
        await clone.Synchronizer.SaveSyncInfoAsync(clone.ToolsFolder, new AgentSyncInfo { ComponentCollectionId = Guid.NewGuid(), DataverseEndpoint = HostedCloud.Organization, EnvironmentId = HostedCloud.EnvironmentId });
        var content = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => clone.Service.ConnectAndMergeAsync(MergeRequest(clone, SnapshotOf(factory, clone)), clone.Environment.Cloud.Client.Object, CancellationToken.None));

        Assert.Equal(WorkspaceAttachFailure.ConnectBoundToAnotherAgent, failure.Failure);
        Assert.Equal(clone.AgentId, failure.AgentId);
        Assert.Contains($"'{clone.ToolsFolder}'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(content, await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder));
        Assert.Empty(clone.Environment.Island.Saves);
    }

    [Fact]
    public async Task ConnectAndMergeAsync_WhenTheTargetHoldsNoSuchAgent_RefusesBeforeWritingAnything()
    {
        await SeedExistingAgentAsync();
        var content = await FilesOfAsync(_files, Workspace, ToolsFolder);

        var failure = await Assert.ThrowsAsync<WorkspaceAttachException>(() => CreateService().ConnectAndMergeAsync(new WorkspaceConnectMergeRequest { Connect = ConnectRequest(schemaName: "con_missing") }, _environment.Cloud.Client.Object, CancellationToken.None));

        Assert.Equal(WorkspaceAttachFailure.AgentNotFound, failure.Failure);
        Assert.Contains("'con_missing'", failure.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { nameof(ISyncDataverseClient.GetAgentIdBySchemaNameAsync) }, _environment.DataverseCalls());
        Assert.Equal(content, await FilesOfAsync(_files, Workspace, ToolsFolder));
    }

    [Theory]
    [InlineData("topics/Greeting.mcs.yml")]
    [InlineData(".mcs/botdefinition.json")]
    public async Task ConnectAndMergeAsync_WhenAWriteFails_PutsBackEveryFileAndCloudCacheAndRethrows(string unwritable)
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var (clone, baseFiles) = await ConnectedCloneWithChangesAsync(factory, session.Root);
        var content = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);
        var locked = new IOException("The file is locked.");

        Assert.Same(locked, await Assert.ThrowsAsync<IOException>(() => new WorkspaceAttachService(clone.Synchronizer, clone.Synchronizer, clone.Synchronizer, new OperationContextProvider(), new FailingFolderFactory(factory, clone.AgentFolder, locked, new AgentFilePath(unwritable), null), TimeProvider.System).ConnectAndMergeAsync(MergeRequest(clone, baseFiles), clone.Environment.Cloud.Client.Object, CancellationToken.None)));
        Assert.Equal(content, await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder));
    }

    [Fact]
    public async Task ConnectAndMergeAsync_WhenAFileCannotBePutBack_PutsBackTheOthersAndRethrowsTheOriginalFailure()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var (clone, baseFiles) = await ConnectedCloneWithChangesAsync(factory, session.Root);
        var content = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);
        var locked = new IOException("The file is locked.");

        Assert.Same(locked, await Assert.ThrowsAsync<IOException>(() => new WorkspaceAttachService(clone.Synchronizer, clone.Synchronizer, clone.Synchronizer, new OperationContextProvider(), new FailingFolderFactory(factory, clone.AgentFolder, locked, CloudCachePath, new AgentFilePath("topics/Farewell.mcs.yml")), TimeProvider.System).ConnectAndMergeAsync(MergeRequest(clone, baseFiles), clone.Environment.Cloud.Client.Object, CancellationToken.None)));
        var files = await FilesOfAsync(factory, clone.AgentFolder, clone.ToolsFolder);
        Assert.Equal(content, files.Where(file => file.Key != $"{clone.AgentFolder}topics/Farewell.mcs.yml"));
        Assert.Contains($"{clone.AgentFolder}topics/Farewell.mcs.yml", files.Keys);
    }

    [Fact]
    public async Task ConnectAndMergeAsync_ThroughTheSynchronizer_ClonesInMemoryAndOpensNoFolderButTheConnectedOnes()
    {
        var factory = new RecordingFileAccessorFactory();
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        clone.Environment.Island.Seed(clone.AgentId, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Farewell", "Farewell"));
        var baseFiles = SnapshotOf(factory, clone);
        while (factory.Opened.TryDequeue(out _))
        {
        }

        var result = await clone.Service.ConnectAndMergeAsync(MergeRequest(clone, baseFiles), clone.Environment.Cloud.Client.Object, CancellationToken.None);

        Assert.Equal(new[] { ("Contoso Helpdesk/topics/Farewell.mcs.yml", WorkingCopyMergeOutcome.Added) }, result.Outcomes.Select(outcome => (outcome.Key, outcome.Value)));
        Assert.All(factory.Opened, root => Assert.Contains(root, new[] { clone.AgentFolder.ToString(), clone.ToolsFolder.ToString() }));
    }

    [Fact]
    public async Task ConnectAndMergeAsync_WithASynchronizerThatIsNotSyncs_ClonesThroughItIntoAWorkspaceOfTheFileFactory()
    {
        using var factory = new ProductionFileAccessorFactory(requireSession: true);
        using var session = factory.LeaseTemporaryWorkspace("mcs-connect-");
        var clone = await CloneWithoutCloudCacheAsync(factory, session.Root);
        clone.Environment.Island.Seed(clone.AgentId, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Farewell", "Farewell"));
        var cloned = new List<DirectoryPath>();
        var synchronizer = new Mock<IWorkspaceSynchronizer>(MockBehavior.Strict);
        synchronizer.Setup(sync => sync.SaveSyncInfoAsync(It.IsAny<DirectoryPath>(), It.IsAny<AgentSyncInfo>())).Returns<DirectoryPath, AgentSyncInfo>(clone.Synchronizer.SaveSyncInfoAsync);
        synchronizer.Setup(sync => sync.IsSyncInfoAvailable(It.IsAny<DirectoryPath>())).Returns<DirectoryPath>(clone.Synchronizer.IsSyncInfoAvailable);
        synchronizer.Setup(sync => sync.ApplyTouchupsAsync(It.IsAny<DirectoryPath>(), It.IsAny<ReferenceTracker>(), It.IsAny<CancellationToken>())).Returns<DirectoryPath, ReferenceTracker, CancellationToken>(clone.Synchronizer.ApplyTouchupsAsync);
        synchronizer.Setup(sync => sync.CloneChangesAsync(It.IsAny<DirectoryPath>(), It.IsAny<ReferenceTracker>(), It.IsAny<AuthoringOperationContextBase>(), It.IsAny<ISyncDataverseClient>(), It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).Returns<DirectoryPath, ReferenceTracker, AuthoringOperationContextBase, ISyncDataverseClient, AgentSyncInfo, CancellationToken>((folder, tracker, context, client, syncInfo, cancellationToken) =>
        {
            cloned.Add(folder);
            return clone.Synchronizer.CloneChangesAsync(folder, tracker, context, client, syncInfo, cancellationToken);
        });

        var result = await new WorkspaceAttachService(synchronizer.Object, clone.Synchronizer, clone.Synchronizer, new OperationContextProvider(), factory, TimeProvider.System).ConnectAndMergeAsync(MergeRequest(clone, SnapshotOf(factory, clone)), clone.Environment.Cloud.Client.Object, CancellationToken.None);

        Assert.Equal(new[] { ("Contoso Helpdesk/topics/Farewell.mcs.yml", WorkingCopyMergeOutcome.Added) }, result.Outcomes.Select(outcome => (outcome.Key, outcome.Value)));
        Assert.Equal(2, cloned.Count);
        Assert.DoesNotContain(cloned, folder => folder == clone.AgentFolder || folder == clone.ToolsFolder);
        Assert.Equal(clone.AgentId, (await clone.Synchronizer.GetSyncInfoAsync(clone.AgentFolder)).AgentId);
    }

    [Fact]
    public async Task ConnectAndMergeAsync_WithoutARequestOrAClient_Throws()
    {
        var service = CreateService();

        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => service.ConnectAndMergeAsync(null!, _environment.Cloud.Client.Object, CancellationToken.None))).ParamName);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => service.ConnectAndMergeAsync(new WorkspaceConnectMergeRequest { Connect = null! }, _environment.Cloud.Client.Object, CancellationToken.None))).ParamName);
        Assert.Equal("dataverseClient", (await Assert.ThrowsAsync<ArgumentNullException>(() => service.ConnectAndMergeAsync(new WorkspaceConnectMergeRequest { Connect = ConnectRequest() }, null!, CancellationToken.None))).ParamName);
        Assert.Empty(_environment.Cloud.Client.Invocations);
    }

    public static TheoryData<WorkspaceConnectMergeRequest> IncompleteMergeRequests() => new()
    {
        new WorkspaceConnectMergeRequest { Connect = ConnectRequest(schemaName: " ") },
        new WorkspaceConnectMergeRequest { Connect = ConnectRequest(collectionFolders: new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = Workspace }) },
        new WorkspaceConnectMergeRequest { Connect = ConnectRequest(), BaseFiles = null! },
        new WorkspaceConnectMergeRequest { Connect = ConnectRequest(collectionFolders: new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = new("elsewhere/Shared Tools/") }) },
    };

    [Theory]
    [MemberData(nameof(IncompleteMergeRequests))]
    public async Task ConnectAndMergeAsync_WithAnIncompleteRequest_RefusesBeforeTouchingAnything(WorkspaceConnectMergeRequest request)
    {
        await SeedExistingAgentAsync();
        var content = await FilesOfAsync(_files, Workspace, ToolsFolder);

        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentException>(() => CreateService().ConnectAndMergeAsync(request, _environment.Cloud.Client.Object, CancellationToken.None))).ParamName);
        Assert.Empty(_environment.Cloud.Client.Invocations);
        Assert.Equal(content, await FilesOfAsync(_files, Workspace, ToolsFolder));
    }

    private static void FailRead(ConnectableClone clone, string failingRead, Exception failure)
    {
        if (failingRead == nameof(ISyncDataverseClient.DownloadAllWorkflowsForAgentAsync))
        {
            clone.Environment.Cloud.Client.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        }
        else if (failingRead == nameof(ISyncDataverseClient.DownloadAllAIPromptsForAgentAsync))
        {
            clone.Environment.Cloud.Client.Setup(client => client.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        }
        else
        {
            clone.Environment.Island.Pauses = context => context is BotComponentCollectionAuthoringOperationContext ? Task.FromException(failure) : Task.CompletedTask;
        }
    }

    private static async Task MakeStaleAsync(IFileAccessor folder)
    {
        await WriteAsync(folder, CloudCachePath.ToString(), "{\"stale\":true}");
        await WriteAsync(folder, ".mcs/botdefinition.yml", "stale: true\n");
        await WriteAsync(folder, ChangeTokenPath.ToString(), "token-stale");
        await WriteAsync(folder, ".mcs/.connectors-sync.json", "{}");
        await WriteAsync(folder, ".mcs/workflow-state.json", "\uFEFF{\r\n}");
    }

    internal static async Task<AttachOutcome> AttachInMemoryAsync(IFileAccessorFactory factory, DirectoryPath root, bool retarget)
    {
        var workspace = root.GetChildDirectoryPath("Contoso Helpdesk/");
        var island = new FakeIsland();
        var cloud = new FakeDataverseCloud { ExistingAgentId = retarget ? ExistingAgentId : Guid.Empty };
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), factory, island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var targetContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var contexts = new Mock<IOperationContextProvider>();
        contexts.Setup(provider => provider.GetAsync(It.IsAny<AgentSyncInfo>())).ReturnsAsync(targetContext);
        var accessor = factory.Create(workspace);

        if (retarget)
        {
            var sourceContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
            var sourceSyncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid(), DataverseEndpoint = new Uri("https://source.crm.dynamics.com/"), EnvironmentId = "env-source" };
            await island.Service.Object.SaveChangesAsync(sourceContext, Inserting("cree9_agent.topic.Source", "Source"), CancellationToken.None);
            await island.Service.Object.SaveChangesAsync(targetContext, Inserting("cree9_agent.topic.Target", "Target"), CancellationToken.None);
            await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), sourceContext, new FakeDataverseCloud().Client.Object, sourceSyncInfo, CancellationToken.None);
            await synchronizer.SaveSyncInfoAsync(workspace, sourceSyncInfo);
            await WriteAsync(accessor, "connectionreferences.mcs.yml", WeatherDeclaration);
        }
        else
        {
            AgentScaffolder.Scaffold(accessor, AgentAuthoringModes.CliCopilot, "Contoso Helpdesk", SchemaName, "Be helpful.");
            await WriteAsync(accessor, $"infrastructure/connections/{WeatherReference}.sync.yaml", WeatherDeclaration);
        }

        var workflowFolder = WorkflowWorkspace.RelativeFolderFor("Weather", WorkflowId);
        await WriteAsync(accessor, $"{workflowFolder}/{WorkflowWorkspace.DefinitionFileName}", WeatherDefinition);
        await WriteAsync(accessor, $"{workflowFolder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new WorkflowMetadata { WorkflowId = WorkflowId, Name = "Weather", StateCode = 1, StatusCode = 2 }));

        var result = await new WorkspaceAttachService(synchronizer, synchronizer, synchronizer, contexts.Object, factory, TimeProvider.System).AttachAsync(new WorkspaceAttachRequest { WorkspaceFolder = workspace, DataverseEndpoint = DataverseEndpoint, EnvironmentId = "env-1", TenantId = TenantId, DisplayName = "Contoso Helpdesk", SchemaName = SchemaName, AllowRetarget = retarget }, cloud.Client.Object, CancellationToken.None);

        var fresh = root.GetChildDirectoryPath("Fresh/");
        await synchronizer.CloneChangesAsync(fresh, new ReferenceTracker(), targetContext, cloud.Client.Object, result.SyncInfo, CancellationToken.None);

        return new AttachOutcome(result, cloud, await CacheOfAsync(accessor), await CacheOfAsync(factory.Create(fresh)), accessor.Exists(RetargetBackupPath), await synchronizer.GetSyncInfoAsync(workspace));
    }

    private static PvaComponentChangeSet Inserting(string schemaName, string displayName) => new(new BotComponentChange[] { new BotComponentInsert(PushComponentCollectionExclusionTests.Topic(schemaName, displayName)) }, null, null);

    private static async Task<string> CacheOfAsync(IFileAccessor workspace) => await workspace.ReadStringAsync(CloudCachePath, CancellationToken.None) + "\n" + await workspace.ReadStringAsync(ChangeTokenPath, CancellationToken.None);

    private static async Task WriteAsync(IFileAccessor workspace, string relativePath, string content) => await workspace.WriteAsync(new AgentFilePath(relativePath), Encoding.UTF8.GetBytes(content), CancellationToken.None);

    private static WorkspaceAttachRequest Request(bool allowRetarget = false, Guid? expectedAgentId = null, bool persistRetargetBackup = false, bool createIfMissing = true, bool pushAuxiliaryContent = true, string displayName = "Contoso Helpdesk") => new()
    {
        WorkspaceFolder = Workspace,
        DataverseEndpoint = DataverseEndpoint,
        EnvironmentId = "env-1",
        TenantId = TenantId,
        DisplayName = displayName,
        SchemaName = SchemaName,
        AllowRetarget = allowRetarget,
        ExpectedAgentId = expectedAgentId,
        PersistRetargetBackup = persistRetargetBackup,
        CreateIfMissing = createIfMissing,
        PushAuxiliaryContent = pushAuxiliaryContent,
    };

    private T Record<T>(string call, T value)
    {
        _calls.Add(call);
        return value;
    }

    private void RemoveScaffold()
    {
        foreach (var path in _scaffolded)
        {
            _workspace.Delete(new AgentFilePath(path));
        }
    }

    private WorkspaceAttachService CreateService() => new(_synchronizer.Object, _retarget.Object, _cacheWriter.Object, _contexts.Object, _files, TimeProvider.System);

    private Task<WorkspaceAttachResult> AttachAsync(WorkspaceAttachRequest? request = null) => CreateService().AttachAsync(request ?? Request(), _cloud.Client.Object, CancellationToken.None);

    private Task<WorkspaceConnectResult> ConnectAsync(WorkspaceConnectRequest? request = null, CancellationToken cancellationToken = default) => new WorkspaceAttachService(_synchronizer.Object, new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), _files, new FakeIsland().Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver()), _cacheWriter.Object, _contexts.Object, _files, TimeProvider.System).ConnectAsync(request ?? ConnectRequest(), _environment.Cloud.Client.Object, cancellationToken);

    private async Task<Guid> SeedExistingAgentAsync()
    {
        var agentId = _environment.SeedAgent(SchemaName, "Contoso Helpdesk");
        _environment.Link(agentId, _environment.SeedCollection(ToolsSchemaName, "Shared Tools"));
        await WriteAsync(_files.Create(ToolsFolder), "collection.mcs.yml", $"schemaName: {ToolsSchemaName}\ndisplayName: Shared Tools\n");

        return agentId;
    }

    private static WorkspaceConnectRequest ConnectRequest(string schemaName = SchemaName, IReadOnlyDictionary<string, DirectoryPath>? collectionFolders = null, bool namesTheEndpoint = true, string environmentId = "env-1") => new()
    {
        WorkspaceFolder = Workspace,
        DataverseEndpoint = namesTheEndpoint ? DataverseEndpoint : null,
        EnvironmentId = environmentId,
        TenantId = TenantId,
        SchemaName = schemaName,
        CollectionFolders = collectionFolders ?? Tools,
    };

    private async Task BindAsync(DirectoryPath folder, Guid? agentId, Guid? collectionId = null, string environmentId = "env-1")
    {
        await WriteAsync(_files.Create(folder), SyncInfoPath.ToString(), $"{{\"environmentId\":\"{environmentId}\"}}");
        _bindings[folder] = new AgentSyncInfo { AgentId = agentId, ComponentCollectionId = collectionId, DataverseEndpoint = DataverseEndpoint, EnvironmentId = environmentId };
    }

    private static async Task<ConnectableClone> CloneWithoutCloudCacheAsync(IFileAccessorFactory factory, DirectoryPath root)
    {
        var environment = new HostedCloud();
        var agentId = environment.SeedAgent(SchemaName, "Contoso Helpdesk", PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Greeting", "Greeting"));
        var toolsId = environment.SeedCollection(ToolsSchemaName, "Shared Tools", PushComponentCollectionExclusionTests.Topic($"{ToolsSchemaName}.topic.Lookup", "Lookup"));
        environment.Link(agentId, toolsId);
        environment.Cloud.AddConnectionReference(WeatherReference, "/providers/Microsoft.PowerApps/apis/shared_msnweather");
        environment.AddWorkflow(agentId, new WorkflowMetadata { WorkflowId = WorkflowId, Name = "Weather", ClientData = WeatherDefinition, StateCode = 1, StatusCode = 2 });
        environment.AddPrompt(agentId, new AIPromptMetadata { AIModelId = Guid.NewGuid(), Name = "Summarize", CustomConfiguration = "{\"version\":\"1\"}" });
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), factory, environment.Island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var contexts = new OperationContextProvider();
        var agentFolder = root.GetChildDirectoryPath("Contoso Helpdesk/");
        var toolsFolder = root.GetChildDirectoryPath("Shared Tools/");
        var syncInfo = await WorkspaceCloneSequence.ForAgentAsync(environment.Cloud.Client.Object, agentId, HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, CancellationToken.None);
        await WorkspaceCloneSequence.CloneAsync(synchronizer, new[] { new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(await contexts.GetAsync(syncInfo), agentFolder), new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(await contexts.GetAsync(WorkspaceCloneSequence.ForComponentCollection(syncInfo, toolsId)), toolsFolder) }, syncInfo, environment.Cloud.Client.Object, CancellationToken.None);
        var clonedCache = (await FilesOfAsync(factory, agentFolder, toolsFolder)).Where(file => IsCloudCache(file.Key)).ToArray();
        factory.Create(agentFolder).DeleteDirectory(new AgentFilePath(WorkspaceSynchronizer.HiddenRoot));
        factory.Create(toolsFolder).DeleteDirectory(new AgentFilePath(WorkspaceSynchronizer.HiddenRoot));
        environment.Cloud.Client.Invocations.Clear();

        return new ConnectableClone(environment, synchronizer, new WorkspaceAttachService(synchronizer, synchronizer, synchronizer, contexts, factory, TimeProvider.System), new WorkspaceConnectRequest { WorkspaceFolder = agentFolder, DataverseEndpoint = HostedCloud.Organization, EnvironmentId = HostedCloud.EnvironmentId, TenantId = HostedCloud.TenantId, SchemaName = SchemaName, CollectionFolders = new Dictionary<string, DirectoryPath> { [ToolsSchemaName] = toolsFolder } }, agentFolder, toolsFolder, agentId, toolsId, clonedCache);
    }

    private static async Task<ConnectableClone> ConnectedCloneAsync(IFileAccessorFactory factory, DirectoryPath root)
    {
        var clone = await CloneWithoutCloudCacheAsync(factory, root);
        await clone.Service.ConnectAsync(clone.Request, clone.Environment.Cloud.Client.Object, CancellationToken.None);
        clone.Environment.Cloud.Client.Invocations.Clear();

        return clone;
    }

    private static async Task<SortedDictionary<string, string>> FilesOfAsync(IFileAccessorFactory factory, params DirectoryPath[] folders)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var folder in folders)
        {
            var accessor = factory.Create(folder);

            foreach (var path in accessor.ListFiles())
            {
                files[$"{folder}{path}"] = Encoding.UTF8.GetString(await accessor.ReadBytesAsync(path, CancellationToken.None));
            }
        }

        return files;
    }

    private static bool IsCloudCache(string path) => path.Contains($"/{WorkspaceSynchronizer.HiddenRoot}/", StringComparison.Ordinal);

    private static async Task<(ConnectableClone Clone, Dictionary<string, ReadOnlyMemory<byte>> BaseFiles)> ConnectedCloneWithChangesAsync(IFileAccessorFactory factory, DirectoryPath root)
    {
        var clone = await ConnectedCloneAsync(factory, root);
        var agent = factory.Create(clone.AgentFolder);
        var baseFiles = SnapshotOf(factory, clone);
        clone.Environment.Island.Seed(clone.AgentId, PushComponentCollectionExclusionTests.Topic($"{SchemaName}.topic.Farewell", "Farewell"));
        await KeepInBaseAndLocallyAsync(agent, baseFiles, "Contoso Helpdesk", "topics/Greeting.mcs.yml", "kind: Old\n");
        await KeepInBaseAndLocallyAsync(agent, baseFiles, "Contoso Helpdesk", "topics/Retired.mcs.yml", "kind: AdaptiveDialog\n");

        return (clone, baseFiles);
    }

    private static WorkspaceConnectMergeRequest MergeRequest(ConnectableClone clone, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> baseFiles) => new() { Connect = clone.Request, BaseFiles = baseFiles };

    private static Dictionary<string, ReadOnlyMemory<byte>> SnapshotOf(IFileAccessorFactory factory, ConnectableClone clone) => WorkspaceSnapshotFiles.Read("Contoso Helpdesk", new[] { new KeyValuePair<string, IFileAccessor>("Contoso Helpdesk", factory.Create(clone.AgentFolder)), new KeyValuePair<string, IFileAccessor>("Shared Tools", factory.Create(clone.ToolsFolder)) }).Files.ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);

    private static string TextOf(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, string path) => Encoding.UTF8.GetString(files[path].ToArray());

    private static async Task KeepInBaseAndLocallyAsync(IFileAccessor folder, Dictionary<string, ReadOnlyMemory<byte>> baseFiles, string folderName, string path, string text)
    {
        baseFiles[$"{folderName}/{path}"] = Encoding.UTF8.GetBytes(text);
        await WriteAsync(folder, path, text);
    }

    private sealed class FailingFolderFactory : IFileAccessorFactory
    {
        private readonly IFileAccessorFactory _files;

        private readonly DirectoryPath _folder;

        private readonly Exception _failure;

        private readonly AgentFilePath? _unwritable;

        private readonly AgentFilePath? _undeletable;

        public FailingFolderFactory(IFileAccessorFactory files, DirectoryPath folder, Exception failure, AgentFilePath? unwritable, AgentFilePath? undeletable)
        {
            _files = files;
            _folder = folder;
            _failure = failure;
            _unwritable = unwritable;
            _undeletable = undeletable;
        }

        public bool IsMemoryBacked => _files.IsMemoryBacked;

        public IFileAccessor Create(DirectoryPath root) => root == _folder ? new FailingFolder(_files.Create(root), _failure, _unwritable, _undeletable) : _files.Create(root);

        public void Release(DirectoryPath root) => _files.Release(root);
    }

    private sealed class FailingFolder : IFileAccessor
    {
        private readonly IFileAccessor _folder;

        private readonly Exception _failure;

        private readonly AgentFilePath? _unwritable;

        private readonly AgentFilePath? _undeletable;

        public FailingFolder(IFileAccessor folder, Exception failure, AgentFilePath? unwritable, AgentFilePath? undeletable)
        {
            _folder = folder;
            _failure = failure;
            _unwritable = unwritable;
            _undeletable = undeletable;
        }

        public bool Exists(AgentFilePath path) => _folder.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _folder.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => path == _unwritable ? throw _failure : _folder.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => _folder.OpenRead(path);

        public void Delete(AgentFilePath path)
        {
            if (path == _undeletable)
            {
                throw _failure;
            }

            _folder.Delete(path);
        }

        public void DeleteDirectory(AgentFilePath path) => _folder.DeleteDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => _folder.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*") => _folder.ListFiles(relativeFolder, filePattern);
    }
}
