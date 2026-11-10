// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using static Microsoft.CopilotStudio.Sync.UnitTests.DataverseFailureTextTests;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkspacePushServiceTests
{
    private const string Push = nameof(IWorkspaceSynchronizer.PushLocalChangesAsync);

    private const string Refresh = nameof(IWorkspaceRetargetService.SyncWorkspaceAsync);

    private const string UnreadableReply = "Improper response, not implemented";

    private const string DuplicateReference = "[0x80072013:ExportKeyInvalidCreate] violates a database constraint on cre98_AgentC1.cr.WX3p-EQ4";

    private static readonly DirectoryPath Folder = new("operation/Contoso Helpdesk/");

    private static readonly Guid AgentId = new("5d2c7e1a-3b4f-4a6d-8e9c-1f2a3b4c5d6e");

    private static readonly string[] ReadSteps = { nameof(IWorkspaceSynchronizer.GetSyncInfoAsync), nameof(IOperationContextProvider.GetAsync), nameof(IWorkspaceSynchronizer.ReadWorkspaceDefinitionAsync), nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync) };

    private readonly ProductionFileAccessorFactory _files = new();

    private readonly Mock<IWorkspaceSynchronizer> _synchronizer = new(MockBehavior.Strict);

    private readonly Mock<IWorkspaceRetargetService> _retarget = new(MockBehavior.Strict);

    private readonly Mock<IOperationContextProvider> _contexts = new(MockBehavior.Strict);

    private readonly Mock<ISyncDataverseClient> _dataverse = new(MockBehavior.Strict);

    private readonly SteppingTimeProvider _clock = new();

    private readonly AuthoringOperationContext _context = ComponentWriterDefensiveTests.CreateMockOperationContext();

    private readonly CloudFlowMetadata _flowMetadata = new();

    private readonly ImmutableArray<AIPromptMetadata> _prompts = ImmutableArray.Create(new AIPromptMetadata { AIModelId = Guid.NewGuid(), Name = "Summarize" });

    private readonly List<string> _steps = new();

    private readonly List<WorkflowResponse> _workflowResponses = new();

    private readonly List<AIPromptResponse> _promptResponses = new();

    private readonly List<(DefinitionBase Definition, CloudFlowMetadata? FlowMetadata, ImmutableArray<AIPromptMetadata> Prompts)> _pushed = new();

    private readonly List<(CloudFlowMetadata? FlowMetadata, ImmutableArray<AIPromptMetadata> Prompts)> _refreshed = new();

    private readonly List<(string Step, bool OverwriteRemoteChanges)> _overwrites = new();

    private readonly ConnectionReferenceInfo _confirmedReference = new() { ConnectionReferenceId = Guid.NewGuid(), ConnectionReferenceLogicalName = "crf9a_Classic.shared_teams.0add1954", ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_teams" };

    private IReadOnlyDictionary<string, Guid>? _provisionedConnectorIds;

    private Func<int, Exception?> _failPush = _ => null;

    private CustomConnectorPushResult _connectorPush = new() { PushedRowIds = new Dictionary<string, Guid> { ["shared_contoso"] = Guid.NewGuid() } };

    private AgentSyncInfo _syncInfo = new() { AgentId = AgentId };

    private DefinitionBase _definition = new BotDefinition();

    private ImmutableArray<Change> _changes = ImmutableArray<Change>.Empty;

    private Func<Task<ImmutableArray<string>>> _upload = () => Task.FromResult(ImmutableArray.Create("Handbook.pdf"));

    private WorkflowActivationMode? _activationMode;

    public WorkspacePushServiceTests()
    {
        _synchronizer.Setup(synchronizer => synchronizer.GetSyncInfoAsync(Folder)).Returns(() => Step(nameof(IWorkspaceSynchronizer.GetSyncInfoAsync), Task.FromResult(_syncInfo)));
        _contexts.Setup(contexts => contexts.GetAsync(It.IsAny<AgentSyncInfo>())).Returns(() => Step(nameof(IOperationContextProvider.GetAsync), Task.FromResult<AuthoringOperationContextBase>(_context)));
        _synchronizer.Setup(synchronizer => synchronizer.ReadWorkspaceDefinitionAsync(Folder, It.IsAny<CancellationToken>(), true)).Returns(() => Step(nameof(IWorkspaceSynchronizer.ReadWorkspaceDefinitionAsync), Task.FromResult(_definition)));
        _synchronizer.Setup(synchronizer => synchronizer.ThrowIfWorkspaceInvalidAsync(Folder, It.IsAny<DefinitionBase>(), It.IsAny<CancellationToken>())).Returns(() => Step(nameof(IWorkspaceSynchronizer.ThrowIfWorkspaceInvalidAsync), Task.CompletedTask));
        _synchronizer.Setup(synchronizer => synchronizer.GetLocalChangesAsync(Folder, It.IsAny<DefinitionBase>(), _dataverse.Object, It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).Returns(() => Step(nameof(IWorkspaceSynchronizer.GetLocalChangesAsync), Task.FromResult<(PvaComponentChangeSet, ImmutableArray<Change>)>((null!, _changes))));
        _synchronizer.Setup(synchronizer => synchronizer.PushCustomConnectorsAsync(Folder, _dataverse.Object, It.IsAny<CancellationToken>(), It.IsAny<bool>())).Returns<DirectoryPath, ISyncDataverseClient, CancellationToken, bool>((_, _, _, overwriteRemoteChanges) => Step(nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), overwriteRemoteChanges, Task.FromResult(_connectorPush)));
        _synchronizer.Setup(synchronizer => synchronizer.UpsertWorkflowForAgentAsync(Folder, _dataverse.Object, AgentId, It.IsAny<CancellationToken>(), It.IsAny<WorkflowActivationMode>())).Returns<DirectoryPath, ISyncDataverseClient, Guid?, CancellationToken, WorkflowActivationMode>((_, _, _, _, activationMode) =>
        {
            _activationMode = activationMode;
            return Step(nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync), Task.FromResult((_workflowResponses.ToImmutableArray(), _flowMetadata)));
        });
        _synchronizer.Setup(synchronizer => synchronizer.UpsertAIPromptsForAgentAsync(Folder, _dataverse.Object, AgentId, It.IsAny<CancellationToken>(), It.IsAny<bool>())).Returns<DirectoryPath, ISyncDataverseClient, Guid?, CancellationToken, bool>((_, _, _, _, overwriteRemoteChanges) => Step(nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), overwriteRemoteChanges, Task.FromResult((_promptResponses.ToImmutableArray(), _prompts))));
        _synchronizer.Setup(synchronizer => synchronizer.PushLocalChangesAsync(Folder, _context, It.IsAny<DefinitionBase>(), _dataverse.Object, It.IsAny<AgentSyncInfo>(), It.IsAny<CloudFlowMetadata?>(), It.IsAny<ImmutableArray<AIPromptMetadata>>(), It.IsAny<CancellationToken>(), null, It.IsAny<bool>())).Returns<DirectoryPath, AuthoringOperationContextBase, DefinitionBase, ISyncDataverseClient, AgentSyncInfo, CloudFlowMetadata?, ImmutableArray<AIPromptMetadata>, CancellationToken, AuthoringOperationContextBase?, bool>((_, _, definition, _, _, flowMetadata, prompts, _, _, overwriteRemoteChanges) =>
        {
            _pushed.Add((definition, flowMetadata, prompts));
            return Step(Push, overwriteRemoteChanges, _failPush(_pushed.Count) is { } failure ? Task.FromException(failure) : Task.CompletedTask);
        });
        _retarget.Setup(retarget => retarget.SyncWorkspaceAsync(Folder, _context, null, false, _dataverse.Object, It.IsAny<AgentSyncInfo>(), It.IsAny<CloudFlowMetadata?>(), It.IsAny<CancellationToken>(), It.IsAny<ImmutableArray<AIPromptMetadata>>(), false, false, false)).Returns<DirectoryPath, AuthoringOperationContextBase, string?, bool, ISyncDataverseClient, AgentSyncInfo, CloudFlowMetadata?, CancellationToken, ImmutableArray<AIPromptMetadata>, bool, bool, bool>((_, _, _, _, _, _, flowMetadata, _, prompts, _, _, _) =>
        {
            _refreshed.Add((flowMetadata, prompts));
            return Step(Refresh, Task.FromResult(new WorkspaceSyncInfo()));
        });
        _synchronizer.Setup(synchronizer => synchronizer.UploadKnowledgeFilesAsync(Folder, _dataverse.Object, It.IsAny<CancellationToken>())).Returns(() => Step(nameof(IWorkspaceSynchronizer.UploadKnowledgeFilesAsync), _upload()));
    }

    public static TheoryData<Exception, bool, string[]> HealableConnectionReferenceFailures => new()
    {
        { MissingEntity("connectionreference"), false, Array.Empty<string>() },
        { MissingEntity("ConnectionReference"), false, Array.Empty<string>() },
        { new InvalidOperationException("save failed", MissingEntity("connectionreference")), false, Array.Empty<string>() },
        { new InvalidOperationException(DuplicateReference), false, Array.Empty<string>() },
        { MissingEntity("connectionreference"), true, Array.Empty<string>() },
        { MissingEntity("connectionreference"), false, new[] { "Escalate" } },
    };

    public static TheoryData<BotComponentBase[]> ToolsThatNameTheirWorkflows => new()
    {
        new BotComponentBase[] { WorkflowTool("Agent Flow 3", "cli_Agent.tool.AgentFlow3", Guid.NewGuid()), ConnectorTool() },
        new BotComponentBase[] { ConnectorTool() },
    };

    public static TheoryData<Exception, Exception, string> LastingConnectionReferenceFailures => new()
    {
        { MissingEntity("connectionreference"), MissingEntity("connectionreference"), WorkspacePushErrorCodes.ConnectionReferenceMissing },
        { new InvalidOperationException(DuplicateReference), MissingEntity("connectionreference"), WorkspacePushErrorCodes.ConnectionReferenceMissing },
        { MissingEntity("connectionreference"), new InvalidOperationException(DuplicateReference), WorkspacePushErrorCodes.ConnectionReferenceDuplicate },
    };

    public static TheoryData<Exception?> ConnectionReferenceFailuresOnce => new()
    {
        null,
        MissingEntity("connectionreference"),
        new InvalidOperationException(DuplicateReference),
    };

    public static TheoryData<Exception> FailuresNoRereadHeals => new()
    {
        MissingEntity("botcomponent"),
        new InvalidOperationException("connectionreference save failed", MissingEntity("botcomponent")),
        new InvalidOperationException("connectionreference does not exist"),
        new ArgumentException("schemaName is required"),
        new OperationCanceledException(),
        new WorkspacePushException(WorkspacePushErrorCodes.ConnectedAgentMissing, "Unsupported sync operation. A component references a parent agent that could not be created on the cloud.", null),
    };

    [Fact]
    public async Task PushAsync_RunsEveryStepInTheOrderTheServiceNeedsAndReportsWhatItSent()
    {
        _changes = ImmutableArray.Create(new Change { SchemaName = "cree9_agent.topic.Greeting", ChangeKind = "DialogComponent" });
        _workflowResponses.Add(new WorkflowResponse { WorkflowId = Guid.NewGuid(), WorkflowName = "Weather" });
        _promptResponses.Add(new AIPromptResponse { PromptName = "Summarize" });

        var result = await PushAsync();

        Assert.Equal(ReadSteps.Concat(new[] { nameof(IWorkspaceSynchronizer.GetLocalChangesAsync), nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync), nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), Push, nameof(IWorkspaceSynchronizer.UploadKnowledgeFilesAsync) }), _steps);
        Assert.Equal(WorkflowActivationMode.DraftWhenConnectionsUnbound, _activationMode);
        Assert.Equal((_definition, _flowMetadata, _prompts), Assert.Single(_pushed));
        Assert.Equal(_changes, result.Changes);
        Assert.Equal(1, result.AuxiliaryChangeCount);
        Assert.Equal(new[] { "Handbook.pdf" }, result.UploadedFileNames);
        Assert.Same(_definition, result.Definition);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(_clock.Delays);
    }

    [Fact]
    public async Task PushAsync_OfACollectionWorkspace_WritesNoWorkflowsOrPrompts()
    {
        _syncInfo = new AgentSyncInfo { ComponentCollectionId = Guid.NewGuid() };

        var result = await PushAsync();

        Assert.Equal(ReadSteps.Concat(new[] { nameof(IWorkspaceSynchronizer.GetLocalChangesAsync), nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), Push, nameof(IWorkspaceSynchronizer.UploadKnowledgeFilesAsync) }), _steps);
        Assert.Equal((null, true), (_pushed[0].FlowMetadata, _pushed[0].Prompts.IsEmpty));
        Assert.Equal(0, result.AuxiliaryChangeCount);
    }

    [Fact]
    public async Task PushAsync_WhenAWorkflowToolNamesNoWorkflow_RefusesBeforeWritingAnything()
    {
        _definition = new BotDefinition().WithComponents(new BotComponentBase[] { WorkflowTool("Second Broken Tool", "cli_Agent.tool.SecondBroken", default), WorkflowTool("Agent Flow 3", "cli_Agent.tool.AgentFlow3", Guid.NewGuid()), WorkflowTool("Empty Workflow Id Tool", "cli_Agent.tool.EmptyId", Guid.Empty) });

        var failure = await Assert.ThrowsAsync<WorkspacePushException>(() => PushAsync(overwrite: true));

        Assert.Equal(WorkspacePushErrorCodes.WorkflowToolIdEmpty, failure.Code);
        Assert.Equal(new[] { "Empty Workflow Id Tool (cli_Agent.tool.EmptyId)", "Second Broken Tool (cli_Agent.tool.SecondBroken)" }, failure.WorkflowTools);
        Assert.Equal("These workflow tools name no workflow id: Empty Workflow Id Tool (cli_Agent.tool.EmptyId), Second Broken Tool (cli_Agent.tool.SecondBroken). Pull the workflows they call, or remove the tools, before pushing.", failure.Message);
        Assert.Equal(ReadSteps, _steps);
    }

    [Theory]
    [MemberData(nameof(ToolsThatNameTheirWorkflows))]
    public async Task PushAsync_WhenEveryWorkflowToolNamesAWorkflow_PushesTheComponents(BotComponentBase[] components)
    {
        _definition = new BotDefinition().WithComponents(components);

        var result = await PushAsync();

        Assert.Same(_definition, Assert.Single(_pushed).Definition);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task PushAsync_OfAWorkspaceThatDeclaresAConnectionReference_ProvisionsItWithTheConnectorsItPushedBeforeTheWorkflowsAndSendsTheRowTheTargetConfirmed()
    {
        DeclareReference();

        await PushAsync();

        Assert.Equal(ReadSteps.Concat(new[] { nameof(IWorkspaceSynchronizer.GetLocalChangesAsync), nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync), nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), Push, nameof(IWorkspaceSynchronizer.UploadKnowledgeFilesAsync) }), _steps);
        Assert.Same(_connectorPush.PushedRowIds, _provisionedConnectorIds);
        AssertRecordsTheConfirmedRow(Assert.Single(_pushed).FlowMetadata);
    }

    [Fact]
    public async Task PushAsync_WhenTheReplyCannotBeRead_WaitsLongerEachTimeAndRereadsTheCloudCopyBeforeEachResend()
    {
        _failPush = attempt => attempt <= 2 ? new ArgumentException(UnreadableReply) : null;

        await PushAsync();

        Assert.Equal(new[] { Push, Refresh, Push, Refresh, Push }, PushSteps());
        Assert.Equal(new[] { 2, 4 }.Select(seconds => TimeSpan.FromSeconds(seconds)), _clock.Delays);
        Assert.All(_refreshed, refreshed => Assert.Equal((_flowMetadata, _prompts), refreshed));
    }

    [Fact]
    public async Task PushAsync_WhenTheReplyCannotBeReadOnAnyAttempt_GivesUpAfterFourAttemptsNamingTheRefusals()
    {
        var unreadable = new ArgumentException(UnreadableReply);
        _failPush = _ => unreadable;
        _workflowResponses.Add(Refused("Escalate"));

        var failure = await Assert.ThrowsAsync<WorkspacePushException>(() => PushAsync(overwrite: true));

        Assert.Equal((WorkspacePushErrorCodes.ResponseUnreadable, 4, "The service's reply to the push could not be read after 4 attempt(s)."), (failure.Code, failure.Attempts, failure.Message));
        Assert.Same(unreadable, failure.InnerException);
        Assert.Equal("Escalate", Assert.Single(failure.Refusals).Subject);
        Assert.Equal(new[] { Push, Refresh, Push, Refresh, Push, Refresh, Push }, PushSteps());
        Assert.Equal(new[] { 2, 4, 6 }.Select(seconds => TimeSpan.FromSeconds(seconds)), _clock.Delays);
        Assert.DoesNotContain(nameof(IWorkspaceSynchronizer.UploadKnowledgeFilesAsync), _steps);
    }

    [Fact]
    public async Task PushAsync_WhenCanceledWhileWaitingToResend_StopsWithoutResending()
    {
        using var cancellation = new CancellationTokenSource();
        _failPush = _ =>
        {
            cancellation.Cancel();
            return new ArgumentException(UnreadableReply);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PushAsync(cancellationToken: cancellation.Token));

        AssertSentOnceWithoutRereading();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushAsync_PassesOverwriteToTheConnectorsThePromptsAndTheComponents(bool overwrite)
    {
        await PushAsync(overwrite: overwrite);

        Assert.Equal(new[] { (nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), overwrite), (nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), overwrite), (Push, overwrite) }, _overwrites);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushAsync_WhenTheAgentChangedInTheCloud_FailsWithoutRereadingOrResending(bool overwrite)
    {
        var remoteChanged = new WorkspacePushException(WorkspacePushErrorCodes.RemoteChanged, "The agent was changed in the cloud.", null);
        _failPush = _ => remoteChanged;

        Assert.Same(remoteChanged, await Assert.ThrowsAsync<WorkspacePushException>(() => PushAsync(overwrite: overwrite)));
        AssertSentOnceWithoutRereading();
    }

    [Theory]
    [MemberData(nameof(HealableConnectionReferenceFailures))]
    public async Task PushAsync_WhenAConnectionReferenceIsMissingOrDuplicated_RereadsTheCloudCopyAndSendsOnceMore(Exception failure, bool overwrite, string[] refusedWorkflows)
    {
        DeclareReference();
        _workflowResponses.AddRange(refusedWorkflows.Select(Refused));
        _failPush = attempt => attempt == 1 ? failure : null;
        var files = WorkspaceFiles();

        var result = await PushAsync(overwrite: overwrite);

        Assert.Equal(ReadSteps.Concat(new[] { nameof(IWorkspaceSynchronizer.GetLocalChangesAsync), nameof(IWorkspaceSynchronizer.PushCustomConnectorsAsync), nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), nameof(IWorkspaceSynchronizer.UpsertWorkflowForAgentAsync), nameof(IWorkspaceSynchronizer.UpsertAIPromptsForAgentAsync), Push, Refresh, Push, nameof(IWorkspaceSynchronizer.UploadKnowledgeFilesAsync) }), _steps);
        Assert.Equal(new[] { overwrite, overwrite }, _overwrites.Where(step => step.Step == Push).Select(step => step.OverwriteRemoteChanges));
        AssertRecordsTheConfirmedRow(Assert.Single(_refreshed).FlowMetadata);
        Assert.All(_pushed, pushed => Assert.Same(_refreshed[0].FlowMetadata, pushed.FlowMetadata));
        Assert.Equal(refusedWorkflows, result.Diagnostics.Select(diagnostic => diagnostic.Subject));
        Assert.Equal(files, WorkspaceFiles());
        Assert.Empty(_clock.Delays);
    }

    [Theory]
    [MemberData(nameof(LastingConnectionReferenceFailures))]
    public async Task PushAsync_WhenAConnectionReferenceFailureOutlastsTheReread_FailsTypedNamingTheRefusals(Exception first, Exception second, string code)
    {
        _failPush = attempt => attempt == 1 ? first : second;
        _workflowResponses.Add(Refused("Escalate"));

        var failure = await Assert.ThrowsAsync<WorkspacePushException>(() => PushAsync());

        Assert.Equal(code, failure.Code);
        Assert.Same(second, failure.InnerException);
        Assert.EndsWith(second.Message, failure.Message, StringComparison.Ordinal);
        Assert.Equal("Escalate", Assert.Single(failure.Refusals).Subject);
        Assert.Equal(new[] { Push, Refresh, Push }, PushSteps());
    }

    [Theory]
    [MemberData(nameof(FailuresNoRereadHeals))]
    public async Task PushAsync_WithAFailureNoRereadHeals_FailsWithItWithoutResending(Exception failure)
    {
        _failPush = _ => failure;

        Assert.Same(failure, await Assert.ThrowsAnyAsync<Exception>(() => PushAsync(overwrite: true)));
        AssertSentOnceWithoutRereading();
    }

    [Fact]
    public async Task PushAsync_WithATypedPushFailure_NeverRetypesOrResendsItEvenAfterRefusals()
    {
        var connectedAgentMissing = new WorkspacePushException(WorkspacePushErrorCodes.ConnectedAgentMissing, "A component references a parent agent that does not exist.", MissingEntity("connectionreference"));
        _failPush = _ => connectedAgentMissing;
        _workflowResponses.Add(Refused("Escalate"));

        Assert.Same(connectedAgentMissing, await Assert.ThrowsAsync<WorkspacePushException>(() => PushAsync(overwrite: true)));
        AssertSentOnceWithoutRereading();
    }

    [Fact]
    public async Task PushAsync_WhenSomethingIsMissingAfterTheEnvironmentRefusedConnectorsWorkflowsOrPrompts_FailsNamingTheRefusals()
    {
        var missing = new InvalidOperationException("save failed", MissingEntity("botcomponent"));
        _failPush = _ => missing;
        _connectorPush = RefusedConnectors((Connector("Contoso API"), "Failed to push custom connector 'Contoso API': The definition is not valid."));
        _workflowResponses.Add(Refused("Escalate"));
        _workflowResponses.Add(new WorkflowResponse { WorkflowName = "Notify", IsDisabled = true });
        _promptResponses.Add(new AIPromptResponse { PromptName = "Translate", ErrorMessage = "invalid model" });

        var failure = await Assert.ThrowsAsync<WorkspacePushException>(() => PushAsync(overwrite: true));

        Assert.Equal((WorkspacePushErrorCodes.MissingAfterRefusals, "The push failed after the environment refused or disabled 4 custom connector(s), workflow(s) or AI prompt(s) it depends on: save failed"), (failure.Code, failure.Message));
        Assert.Same(missing, failure.InnerException);
        Assert.Equal(new[] { "Contoso API", "Escalate", "Notify", "Translate" }, failure.Refusals.Select(refusal => refusal.Subject));
        AssertSentOnceWithoutRereading();
    }

    [Fact]
    public async Task PushAsync_ReportsEveryConnectorWorkflowAndPromptTheEnvironmentRefusedOrLeftDisabledWithoutTheRawResponse()
    {
        _connectorPush = RefusedConnectors((Connector("Contoso API"), $"Failed to push custom connector 'Contoso API': {Failure().Message}"), (Connector("Fabrikam API"), "Failed to push custom connector 'Fabrikam API': it changed in the cloud since it was last synced."));
        _workflowResponses.Add(new WorkflowResponse { WorkflowName = "Escalate", ErrorMessage = Failure().Message });
        _workflowResponses.Add(new WorkflowResponse { WorkflowName = "Notify", IsDisabled = true });
        _workflowResponses.Add(new WorkflowResponse { WorkflowName = "Weather" });
        _workflowResponses.Add(new WorkflowResponse { WorkflowName = " ", ErrorMessage = "The flow definition is invalid." });
        _promptResponses.Add(new AIPromptResponse { PromptName = "Translate", ErrorMessage = "invalid model" });
        _promptResponses.Add(new AIPromptResponse { PromptName = "Summarize" });

        var result = await PushAsync();

        Assert.Equal(new[]
        {
            new WorkspacePushDiagnostic(WorkspacePushDiagnosticCodes.ConnectorRefused, "Contoso API", null, $"Failed to push custom connector 'Contoso API': {ReportedDescription}"),
            new WorkspacePushDiagnostic(WorkspacePushDiagnosticCodes.ConnectorRefused, "Fabrikam API", null, "Failed to push custom connector 'Fabrikam API': it changed in the cloud since it was last synced."),
            new WorkspacePushDiagnostic(WorkspacePushDiagnosticCodes.WorkflowRefused, "Escalate", ReportedDescription, $"The environment refused the workflow 'Escalate': {ReportedDescription}"),
            new WorkspacePushDiagnostic(WorkspacePushDiagnosticCodes.WorkflowRefused, "(unnamed)", "The flow definition is invalid.", "The environment refused the workflow '(unnamed)': The flow definition is invalid."),
            new WorkspacePushDiagnostic(WorkspacePushDiagnosticCodes.WorkflowNotRunning, "Notify", null, "The environment left the workflow 'Notify' disabled."),
            new WorkspacePushDiagnostic(WorkspacePushDiagnosticCodes.PromptRefused, "Translate", "invalid model", "The environment refused the AI prompt 'Translate': invalid model"),
        }, result.Diagnostics);
        Assert.Equal(4, result.AuxiliaryChangeCount);
    }

    [Fact]
    public async Task PushAsync_LeavesOutOfTheChangesEveryFileOfAConnectorWorkflowOrPromptTheEnvironmentRefused()
    {
        var refusedConnector = Connector("Contoso API");
        var refusedWorkflow = Refused("Escalate");
        var refusedPromptId = Guid.NewGuid();
        var acceptedWorkflowId = Guid.NewGuid();
        var accepted = new[] { new Change { Name = "Greeting", SchemaName = "cree9_agent.topic.Greeting" }, new Change { Name = "Weather", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{acceptedWorkflowId}.metadata" }, Connector("Fabrikam API") };
        _changes = accepted.Concat(new[] { refusedConnector, refusedConnector with { SchemaName = $"{refusedConnector.SchemaName}.openapidefinition" }, new Change { Name = "Escalate", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{refusedWorkflow.WorkflowId}" }, new Change { Name = "Escalate", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{refusedWorkflow.WorkflowId}.metadata" }, new Change { Name = "Translate", SchemaName = $"{AiPromptLayout.SchemaNamePrefix}{refusedPromptId}" }, new Change { Name = "Translate", SchemaName = $"{AiPromptLayout.SchemaNamePrefix}{refusedPromptId}.metadata" } }).ToImmutableArray();
        _connectorPush = RefusedConnectors((refusedConnector, "Failed to push custom connector 'Contoso API': The definition is not valid."));
        _workflowResponses.Add(refusedWorkflow);
        _workflowResponses.Add(new WorkflowResponse { WorkflowId = acceptedWorkflowId, WorkflowName = "Weather" });
        _promptResponses.Add(new AIPromptResponse { AIModelId = refusedPromptId, PromptName = "Translate", ErrorMessage = "invalid model" });

        var result = await PushAsync();

        Assert.Equal(accepted, result.Changes);
    }

    [Fact]
    public async Task PushAsync_CountsOnlyTheWorkflowsItWroteThatNoChangeReportsAndNoPromptOrConnector()
    {
        var reportedWorkflowId = Guid.NewGuid();
        _connectorPush = new CustomConnectorPushResult { PushedRowIds = new Dictionary<string, Guid> { ["shared_contoso"] = Guid.NewGuid() }, Created = new[] { Connector("Contoso API") } };
        _changes = ImmutableArray.Create(new Change { Name = "Escalate", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{reportedWorkflowId}" }, new Change { Name = "Escalate", SchemaName = $"{WorkflowLayout.SchemaNamePrefix}{reportedWorkflowId}.metadata" });
        _workflowResponses.Add(new WorkflowResponse { WorkflowId = reportedWorkflowId, WorkflowName = "Escalate" });
        _workflowResponses.Add(new WorkflowResponse { WorkflowId = Guid.NewGuid(), WorkflowName = "Weather" });
        _promptResponses.Add(new AIPromptResponse { AIModelId = Guid.NewGuid(), PromptName = "Summarize" });

        var result = await PushAsync();

        Assert.Equal(1, result.AuxiliaryChangeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushAsync_RemovesTheFoldersOfRefusedWorkflowsOnlyWhenAsked(bool discardRefusedWorkflows)
    {
        var escalate = Refused("Escalate");
        var weatherId = Guid.NewGuid();
        WriteWorkflow("Escalate", escalate.WorkflowId);
        WriteWorkflow("Weather", weatherId);
        _workflowResponses.Add(escalate);
        _workflowResponses.Add(new WorkflowResponse { WorkflowId = weatherId, WorkflowName = "Weather" });

        await PushAsync(discardRefusedWorkflows: discardRefusedWorkflows);

        Assert.Equal((discardRefusedWorkflows ? new[] { weatherId } : new[] { escalate.WorkflowId, weatherId }).OrderBy(workflowId => workflowId), WorkflowWorkspace.FindAll(_files.Create(Folder)).Select(workflow => workflow.WorkflowId).OrderBy(workflowId => workflowId));
    }

    [Fact]
    public async Task PushAsync_LinksEveryDeclaredCollectionTheDefinitionDoesNotAlreadyLink()
    {
        _definition = new BotDefinition().WithComponentCollections(new[] { Collection("cree9_tools") });

        var result = await PushAsync(declaredCollections: new[] { Collection("CREE9_TOOLS"), Collection("cree9_zeta") });

        Assert.Equal(new[] { "cree9_tools", "cree9_zeta" }, Assert.IsType<BotDefinition>(result.Definition).ComponentCollections.Select(collection => collection.SchemaName.Value));
        Assert.Same(result.Definition, Assert.Single(_pushed).Definition);
    }

    [Fact]
    public async Task PushAsync_WithDeclaredCollectionsTheDefinitionAlreadyLinks_PushesTheDefinitionAsRead()
    {
        _definition = new BotDefinition().WithComponentCollections(new[] { Collection("cree9_tools") });

        Assert.Same(_definition, (await PushAsync(declaredCollections: new[] { Collection("cree9_tools") })).Definition);
    }

    [Fact]
    public async Task PushAsync_WhenTheKnowledgeFilesCannotBeUploaded_FailsNamingHowManyChangesWerePushed()
    {
        var refusal = Failure();
        _changes = ImmutableArray.Create(new Change { SchemaName = "cree9_agent.topic.Greeting" }, new Change { SchemaName = "cree9_agent.topic.Goodbye" });
        _upload = () => Task.FromException<ImmutableArray<string>>(refusal);

        var failure = await Assert.ThrowsAsync<WorkspacePushException>(() => PushAsync());

        Assert.Equal((WorkspacePushErrorCodes.KnowledgeUploadFailed, 2, $"2 component change(s) were pushed, but the knowledge files could not be uploaded: {ReportedDescription}"), (failure.Code, failure.PushedChangeCount, failure.Message));
        Assert.Same(refusal, failure.InnerException);
    }

    [Fact]
    public async Task PushAsync_WhenCanceledWhileUploadingKnowledgeFiles_PropagatesTheCancellation()
    {
        _upload = () => Task.FromException<ImmutableArray<string>>(new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => PushAsync());

        Assert.Equal(nameof(IWorkspaceSynchronizer.UploadKnowledgeFilesAsync), _steps.Last());
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public async Task PushAsync_ThroughTheSynchronizer_OfAClonedCollectionWhoseWorkflowUsesAConnection_KeepsTheCloudsWorkflowsInItsCache(bool changedLocally, bool declaresTheReference, bool connectionReferenceMissingOnce)
    {
        var environment = new HostedCloud();
        var workflowId = Guid.NewGuid();
        var toolsFolder = new DirectoryPath("c:/operation/Shared Tools/");
        var toolsId = environment.SeedCollection("cree9_tools", "Shared Tools", PushComponentCollectionExclusionTests.Topic("cree9_tools.topic.Lookup", "Lookup"));
        environment.Link(environment.SeedAgent(AgentWorkspaceHostPushTests.SchemaName, "Contoso Helpdesk"), toolsId);
        environment.Cloud.AddConnectionReference(WorkspaceAttachServiceTests.WeatherReference, "/providers/Microsoft.PowerApps/apis/shared_msnweather");
        environment.AddWorkflow(toolsId, new WorkflowMetadata { WorkflowId = workflowId, Name = "Weather", ClientData = WorkspaceAttachServiceTests.WeatherDefinition, StateCode = 1, StatusCode = 2 });
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), _files, environment.Island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var contexts = new OperationContextProvider();
        var syncInfo = WorkspaceCloneSequence.ForComponentCollection(await WorkspaceCloneSequence.ForAgentAsync(environment.Cloud.Client.Object, Guid.NewGuid(), HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, CancellationToken.None), toolsId);
        var context = await contexts.GetAsync(syncInfo);
        await WorkspaceCloneSequence.CloneAsync(synchronizer, new[] { new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(context, toolsFolder) }, syncInfo, environment.Cloud.Client.Object, CancellationToken.None);
        var workspace = _files.Create(toolsFolder);
        var clonedFlows = await CachedAsync(workspace, "flows");
        Assert.True(workspace.Exists(new AgentFilePath(ConnectionReferenceStore.FlatFileName)));

        if (!declaresTheReference)
        {
            workspace.Delete(new AgentFilePath(ConnectionReferenceStore.FlatFileName));
        }

        if (changedLocally)
        {
            workspace.Write(new AgentFilePath("topics/LocalLookup.mcs.yml"), Encoding.UTF8.GetBytes(AgentWorkspaceHostPushTests.Topic("LocalLookup")));
        }

        if (connectionReferenceMissingOnce)
        {
            environment.Island.SaveFailures.Enqueue(MissingEntity("connectionreference"));
        }

        await new WorkspacePushService(synchronizer, synchronizer, contexts, _files, _clock).PushAsync(new WorkspacePushRequest { WorkspaceFolder = toolsFolder }, environment.Cloud.Client.Object, CancellationToken.None);

        var (_, localChanges) = await synchronizer.GetLocalChangesAsync(toolsFolder, await synchronizer.ReadWorkspaceDefinitionAsync(toolsFolder, CancellationToken.None), environment.Cloud.Client.Object, syncInfo, CancellationToken.None);
        var (_, remoteChanges) = await synchronizer.GetRemoteChangesAsync(toolsFolder, context, environment.Cloud.Client.Object, syncInfo, CancellationToken.None);
        Assert.Contains(workflowId.ToString(), clonedFlows, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(clonedFlows, await CachedAsync(workspace, "flows"));
        Assert.DoesNotContain(localChanges.Concat(remoteChanges), change => change.SchemaName.Contains(workflowId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(changedLocally ? 1 : 0, environment.Island.SaveCountOf(toolsId));
        Assert.Empty(environment.Island.SaveFailures);
    }

    [Theory]
    [MemberData(nameof(ConnectionReferenceFailuresOnce))]
    public async Task PushAsync_ThroughTheSynchronizer_OfAClonedCollectionWhoseCloudOwnsAnAIPrompt_KeepsTheCloudsPromptsInItsCache(Exception? connectionReferenceFailureOnce)
    {
        var environment = new HostedCloud();
        var promptId = Guid.NewGuid();
        var toolsFolder = new DirectoryPath("c:/operation/Shared Tools/");
        var toolsId = environment.SeedCollection("cree9_tools", "Shared Tools", PushComponentCollectionExclusionTests.Topic("cree9_tools.action.Summarize", "Summarize").WithDialog(CodeSerializer.Deserialize<TaskDialog>($"kind: TaskDialog\nmodelDisplayName: Summarize\nmodelDescription: Summarize\naction:\n  kind: InvokeAIBuilderModelTaskAction\n  aIModelId: {promptId}\n\noutputMode: All\n")!));
        environment.Link(environment.SeedAgent(AgentWorkspaceHostPushTests.SchemaName, "Contoso Helpdesk"), toolsId);
        environment.AddPrompt(toolsId, new AIPromptMetadata { AIModelId = promptId, Name = "Summarize", CustomConfiguration = "{\"version\":\"1\"}" });
        var synchronizer = new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), _files, environment.Island.Service.Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver());
        var contexts = new OperationContextProvider();
        var syncInfo = WorkspaceCloneSequence.ForComponentCollection(await WorkspaceCloneSequence.ForAgentAsync(environment.Cloud.Client.Object, Guid.NewGuid(), HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, CancellationToken.None), toolsId);
        var context = await contexts.GetAsync(syncInfo);
        await WorkspaceCloneSequence.CloneAsync(synchronizer, new[] { new KeyValuePair<AuthoringOperationContextBase, DirectoryPath>(context, toolsFolder) }, syncInfo, environment.Cloud.Client.Object, CancellationToken.None);
        var workspace = _files.Create(toolsFolder);
        var clonedPrompts = await CachedAsync(workspace, "aIModelDefinitions");
        workspace.Write(new AgentFilePath("topics/LocalLookup.mcs.yml"), Encoding.UTF8.GetBytes(AgentWorkspaceHostPushTests.Topic("LocalLookup")));

        if (connectionReferenceFailureOnce is not null)
        {
            environment.Island.SaveFailures.Enqueue(connectionReferenceFailureOnce);
        }

        await new WorkspacePushService(synchronizer, synchronizer, contexts, _files, _clock).PushAsync(new WorkspacePushRequest { WorkspaceFolder = toolsFolder }, environment.Cloud.Client.Object, CancellationToken.None);

        var (_, localChanges) = await synchronizer.GetLocalChangesAsync(toolsFolder, await synchronizer.ReadWorkspaceDefinitionAsync(toolsFolder, CancellationToken.None), environment.Cloud.Client.Object, syncInfo, CancellationToken.None);
        var (_, remoteChanges) = await synchronizer.GetRemoteChangesAsync(toolsFolder, context, environment.Cloud.Client.Object, syncInfo, CancellationToken.None);
        Assert.Contains(promptId.ToString(), clonedPrompts, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(clonedPrompts, await CachedAsync(workspace, "aIModelDefinitions"));
        Assert.DoesNotContain(localChanges.Concat(remoteChanges), change => change.SchemaName.Contains(promptId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, environment.Island.SaveCountOf(toolsId));
        Assert.Empty(environment.Island.SaveFailures);
    }

    private static async Task<string> CachedAsync(IFileAccessor workspace, string property)
    {
        using var cache = JsonDocument.Parse(await workspace.ReadStringAsync(new AgentFilePath(".mcs/botdefinition.json"), CancellationToken.None));

        return cache.RootElement.TryGetProperty(property, out var value) ? value.GetRawText() : string.Empty;
    }

    private static DataverseNotFoundException MissingEntity(string entityName) => new("ObjectDoesNotExist", "0x80040217", "service-request-1", $"Entity '{entityName}' With Id = 2a5f0c1e-0000-0000-0000-000000000001 Does Not Exist", null);

    private static WorkflowResponse Refused(string name) => new() { WorkflowId = Guid.NewGuid(), WorkflowName = name, ErrorMessage = "The flow definition is invalid." };

    private static Change Connector(string name) => new() { Name = name, SchemaName = $"{CustomConnectorLayout.SchemaNamePrefix}{Guid.NewGuid()}" };

    private static CustomConnectorPushResult RefusedConnectors(params (Change Connector, string Reason)[] refusals) => new() { Failed = refusals.Select(refusal => refusal.Connector).ToArray(), FailureReasons = refusals.Select(refusal => refusal.Reason).ToArray() };

    private static DialogComponent WorkflowTool(string displayName, string schemaName, FlowId workflowId) => new DialogComponent.Builder { DisplayName = displayName, SchemaName = new DialogSchemaName(schemaName), Dialog = new WorkflowTool.Builder { WorkflowId = workflowId } }.Build();

    private static DialogComponent ConnectorTool() => new DialogComponent.Builder { DisplayName = "Get a section", SchemaName = new DialogSchemaName("cli_Agent.tool.Getasection_fcU"), Dialog = new ConnectorTool.Builder() }.Build();

    private static BotComponentCollection Collection(string schemaName) => CodeSerializer.Deserialize<BotComponentCollection>($"schemaName: {schemaName}")!;

    private T Step<T>(string step, T value)
    {
        _steps.Add(step);

        return value;
    }

    private T Step<T>(string step, bool overwriteRemoteChanges, T value)
    {
        _overwrites.Add((step, overwriteRemoteChanges));

        return Step(step, value);
    }

    private string[] PushSteps() => _steps.Where(step => step is Push or Refresh).ToArray();

    private void DeclareReference()
    {
        Write(ConnectionReferenceStore.FlatFileName, $"connectionReferences:\n  - connectionReferenceLogicalName: {_confirmedReference.ConnectionReferenceLogicalName}\n    connectorId: {_confirmedReference.ConnectorId}\n");
        _synchronizer.Setup(synchronizer => synchronizer.ProvisionConnectionReferencesAsync(It.IsAny<DefinitionBase>(), _dataverse.Object, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyDictionary<string, Guid>?>())).Returns<DefinitionBase, ISyncDataverseClient, CancellationToken, IReadOnlyDictionary<string, Guid>?>((_, _, _, pushedConnectorIds) =>
        {
            _provisionedConnectorIds = pushedConnectorIds;
            return Step(nameof(IWorkspaceSynchronizer.ProvisionConnectionReferencesAsync), Task.CompletedTask);
        });
        _dataverse.Setup(dataverse => dataverse.GetConnectionReferencesByLogicalNamesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { _confirmedReference });
    }

    private void AssertRecordsTheConfirmedRow(CloudFlowMetadata? flowMetadata)
    {
        var reference = Assert.Single(flowMetadata!.ConnectionReferences);
        Assert.Equal((_confirmedReference.ConnectionReferenceLogicalName, _confirmedReference.ConnectionReferenceId, _confirmedReference.ConnectorId), (reference.ConnectionReferenceLogicalName.Value, reference.Id.Value, reference.ConnectorId.Value));
    }

    private Dictionary<string, string> WorkspaceFiles()
    {
        var workspace = _files.Create(Folder);

        return workspace.ListFiles().ToDictionary(path => path.ToString(), path => Encoding.UTF8.GetString(workspace.ReadBytes(path).ToArray()), StringComparer.Ordinal);
    }

    private void AssertSentOnceWithoutRereading()
    {
        Assert.Equal(new[] { Push }, PushSteps());
        Assert.Empty(_clock.Delays);
    }

    private void WriteWorkflow(string name, Guid workflowId)
    {
        var folder = WorkflowWorkspace.RelativeFolderFor(name, workflowId);
        Write($"{folder}/{WorkflowWorkspace.DefinitionFileName}", "{}");
        Write($"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new WorkflowMetadata { WorkflowId = workflowId, Name = name, StateCode = 1, StatusCode = 2 }));
    }

    private void Write(string path, string content) => _files.Create(Folder).Write(new AgentFilePath(path), Encoding.UTF8.GetBytes(content));

    private Task<WorkspacePushResult> PushAsync(bool overwrite = false, IReadOnlyList<BotComponentCollection>? declaredCollections = null, bool discardRefusedWorkflows = false, CancellationToken cancellationToken = default) => new WorkspacePushService(_synchronizer.Object, _retarget.Object, _contexts.Object, _files, _clock).PushAsync(new WorkspacePushRequest { WorkspaceFolder = Folder, Overwrite = overwrite, DeclaredCollections = declaredCollections ?? Array.Empty<BotComponentCollection>(), DiscardRefusedWorkflows = discardRefusedWorkflows }, _dataverse.Object, cancellationToken);
}
