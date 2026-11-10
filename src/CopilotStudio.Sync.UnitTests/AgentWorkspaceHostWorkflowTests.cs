// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.CopilotStudio.Sync.Hosting;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostWorkflowTests
{
    private const string TeamsReference = "new_sharedteams_77777";

    private const string CloudDefinition = "{\"properties\":{\"from\":\"cloud\"}}";

    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly RecordingAuthProvider _authentication = new();

    private readonly AgentWorkspaceHost _host;

    public AgentWorkspaceHostWorkflowTests() => _host = _cloud.CreateHost(_files);

    [Fact]
    public async Task PushWorkflowAsync_OfAFlowTheEnvironmentLacks_InsertsItUnderItsFolderIdentifierAndARetryFindsIt()
    {
        var workflowId = Guid.NewGuid();
        var snapshot = Scaffold("Weather", workflowId);

        var pushed = await PushAsync(snapshot);
        var pushedCache = _files.TakeCache("Weather");
        var retried = await PushAsync(snapshot);
        var retriedCache = _files.TakeCache("Weather");
        await CloneAsync(workflowId);

        Assert.True(pushed.Activated);
        Assert.Empty(pushed.Diagnostics);
        Assert.Single(_cloud.StandaloneWorkflows.Calls, call => call == "insert");
        Assert.Equal(("Weather", 1), (_cloud.StandaloneWorkflows.Stored(workflowId)!.Name, _cloud.StandaloneWorkflows.Stored(workflowId)!.StateCode));
        Assert.Equal(pushedCache, retriedCache);
        Assert.Equal(pushedCache, _files.TakeCache("Weather"));
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task PushWorkflowAsync_OverAnUnpublishedDesignerDraft_OverwritesItAndNamesTheFlow()
    {
        var workflowId = Guid.NewGuid();
        _cloud.StandaloneWorkflows.Seed(Row(workflowId));
        _cloud.StandaloneWorkflows.Drafts.Add(workflowId);

        var result = await PushAsync(Scaffold("Weather", workflowId));
        var pushedCache = _files.TakeCache("Weather");
        await CloneAsync(workflowId);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(AgentSyncDiagnosticCodes.UnpublishedDraftOverwritten, diagnostic.Code);
        Assert.Equal("Weather", diagnostic.Subject);
        Assert.Contains($"'Weather' ({workflowId})", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cloud", _cloud.StandaloneWorkflows.Stored(workflowId)!.ClientData!, StringComparison.Ordinal);
        Assert.Equal(pushedCache, _files.TakeCache("Weather"));
    }

    [Fact]
    public async Task PushWorkflowAsync_WhenTheEnvironmentRefusesToStartTheFlow_ReportsTheRefusalAgainstTheFlowWithoutTheServicesResponse()
    {
        _cloud.StandaloneWorkflows.StartFailure = DataverseFailureTextTests.ReportedBody;

        var refusal = Assert.Single((await PushAsync(Scaffold("Weather", Guid.NewGuid()))).Diagnostics);
        Assert.Equal((AgentSyncDiagnosticCodes.ActivationRefused, "Weather", $"Workflow 'Weather' was saved, but the environment refused to have it enabled ({DataverseFailureTextTests.ReportedDescription})."), (refusal.Code, refusal.Subject, refusal.Message));
        Assert.Null(refusal.Detail);
    }

    [Fact]
    public async Task PushWorkflowAsync_WhenTheEnvironmentRefusesToUpdateOverADraft_ReplacesTheFlowUnderItsIdentifier()
    {
        var workflowId = Guid.NewGuid();
        _cloud.StandaloneWorkflows.Seed(Row(workflowId));
        _cloud.StandaloneWorkflows.UpdateFailure = StandaloneWorkflowDownloadGuardTests.DraftRefusal;

        var result = await PushAsync(Scaffold("Weather", workflowId));
        var pushedCache = _files.TakeCache("Weather");
        await CloneAsync(workflowId);

        Assert.Equal(AgentSyncDiagnosticCodes.UnpublishedDraftOverwritten, Assert.Single(result.Diagnostics).Code);
        Assert.True(result.Activated);
        Assert.Equal(new[] { "update", "delete", "insert" }, _cloud.StandaloneWorkflows.Calls.Where(call => call is "update" or "delete" or "insert"));
        Assert.DoesNotContain("cloud", _cloud.StandaloneWorkflows.Stored(workflowId)!.ClientData!, StringComparison.Ordinal);
        Assert.Equal(pushedCache, _files.TakeCache("Weather"));
    }

    [Fact]
    public async Task PushWorkflowAsync_WithItsConnectionBound_StartsTheFlow()
    {
        var workflowId = Guid.NewGuid();
        _cloud.Cloud.AddConnectionReference(TeamsReference, "/providers/Microsoft.PowerApps/apis/shared_teams", "conn-teams");

        var result = await PushAsync(With(Scaffold("Notify", workflowId), $"Notify/{WorkflowWorkspace.RelativeFolderFor("Notify", workflowId)}/{WorkflowWorkspace.DefinitionFileName}", Definition("shared_teams", TeamsReference)));
        var pushedCache = _files.TakeCache("Notify");
        await _host.CloneWorkflowAsync(new WorkflowCloneRequest(Target(workflowId), "Notify"), _authentication, CancellationToken.None);

        Assert.True(result.Activated);
        Assert.Empty(result.UnboundConnectionReferences);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("conn-teams", _cloud.Cloud.ConnectionReferences[TeamsReference].ConnectionId);
        Assert.Equal(1, _cloud.StandaloneWorkflows.Stored(workflowId)!.StateCode);
        Assert.Equal(pushedCache, _files.TakeCache("Notify"));
    }

    [Fact]
    public async Task PushWorkflowAsync_WithItsConnectionUnbound_LeavesTheFlowInDraftAndNamesTheReference()
    {
        var workflowId = Guid.NewGuid();

        var result = await PushAsync(With(Scaffold("Notify", workflowId), $"Notify/{WorkflowWorkspace.RelativeFolderFor("Notify", workflowId)}/{WorkflowWorkspace.DefinitionFileName}", Definition("shared_teams", TeamsReference)));

        Assert.False(result.Activated);
        Assert.Equal(new[] { TeamsReference }, result.UnboundConnectionReferences);
        Assert.Equal(0, _cloud.StandaloneWorkflows.Stored(workflowId)!.StateCode);
    }

    [Fact]
    public async Task PushWorkflowAsync_OfAFolderNamingAnotherWorkflow_RefusesBeforeAnyWrite()
    {
        var snapshot = Scaffold("Weather", Guid.NewGuid());

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _host.PushWorkflowAsync(snapshot, Target(Guid.NewGuid()), HostedCloud.Options(), _authentication, CancellationToken.None));

        Assert.Equal(AgentWorkspaceHostErrorCodes.SnapshotInvalid, failure.Code);
        Assert.Equal(AgentSyncDiagnosticCodes.WorkflowIdMismatch, Assert.Single(failure.Diagnostics).Code);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task PushWorkflowAsync_OfAFolderHoldingTwoWorkflows_RefusesBeforeAnyWrite()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var snapshot = new AgentWorkspaceSnapshot("Weather", Scaffold("Weather", first).Files.Concat(Scaffold("Weather", second).Files.Where(file => file.Key.Contains(second.ToString(), StringComparison.OrdinalIgnoreCase))).ToDictionary(file => file.Key, file => file.Value));

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => PushAsync(snapshot));

        Assert.Equal(AgentWorkspaceHostErrorCodes.SnapshotInvalid, failure.Code);
        Assert.Equal(AgentSyncDiagnosticCodes.WorkflowProjectInvalid, Assert.Single(failure.Diagnostics).Code);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls.Where(call => call is not "target"));
    }

    [Theory]
    [InlineData("Other/readme.md", AgentSyncDiagnosticCodes.OutsideDeclaredFolders)]
    [InlineData("Weather/.mcs/botdefinition.json", AgentSyncDiagnosticCodes.SyncStateInPath)]
    [InlineData("Weather/workflows/../readme.md", AgentSyncDiagnosticCodes.RelativeSegmentInPath)]
    [InlineData("Weather/workflows/LPT1.json", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData("Weather/Aux/readme.md", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData("Weather/workflows/read|me.md", AgentSyncDiagnosticCodes.UnsafePath)]
    public async Task PushWorkflowAsync_WithAPathAPushMustNotWrite_RefusesBeforeAnyCloudCall(string path, string code)
    {
        var snapshot = With(Scaffold("Weather", Guid.NewGuid()), path, "{}");

        var failure = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => PushAsync(snapshot));

        Assert.Equal(AgentWorkspaceHostErrorCodes.SnapshotInvalid, failure.Code);
        Assert.Equal(code, Assert.Single(failure.Diagnostics).Code);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task CloneWorkflowAsync_WritesTheFlowWithoutSyncStateAndPushesBackUnchanged()
    {
        var workflowId = Guid.NewGuid();
        _cloud.StandaloneWorkflows.Seed(Row(workflowId));

        var clone = await CloneAsync(workflowId);
        var pushed = await _host.PushWorkflowAsync(clone.Snapshot, Target(workflowId), HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Equal(workflowId.ToString(), clone.WorkflowId);
        Assert.Equal("Weather", clone.Snapshot.Folder);
        Assert.Contains($"Weather/{WorkflowWorkspace.RelativeFolderFor("Weather", workflowId)}/{WorkflowWorkspace.DefinitionFileName}", clone.Snapshot.Files.Keys);
        Assert.DoesNotContain(clone.Snapshot.Files.Keys, SyncStatePaths.IsSyncStatePath);
        Assert.Empty(pushed.Diagnostics);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls.Where(call => call is "insert" or "update" or "delete"));
        Assert.Equal(0, _files.LiveCount);
    }

    [Fact]
    public async Task CloneWorkflowAsync_OfAFlowTheEnvironmentLacks_ThrowsWorkflowNotFound()
    {
        Assert.Equal(AgentWorkspaceHostErrorCodes.WorkflowNotFound, (await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => CloneAsync(Guid.NewGuid()))).Code);
        Assert.Equal(0, _files.LiveCount);
    }

    [Theory]
    [InlineData(null, "Weather")]
    [InlineData("not-an-identifier", "Weather")]
    [InlineData("8c1c1d0e-0f5e-4b8e-9d0c-7b7d6c5b4a39", "Weather/Nested")]
    public async Task CloneWorkflowAsync_WithoutAnIdentifierOrATopLevelFolder_ThrowsBeforeAnyCloudCall(string? workflowId, string folder)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.CloneWorkflowAsync(new WorkflowCloneRequest(new WorkflowSyncTarget(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, workflowId), folder), _authentication, CancellationToken.None));

        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_files.Opened);
    }

    [Fact]
    public async Task CloneWorkflowAsync_KeepsTheFlowsName()
    {
        var workflowId = Guid.NewGuid();
        _cloud.StandaloneWorkflows.Seed(Row(workflowId));

        var clone = await CloneAsync(workflowId);

        Assert.Contains($"Weather/{WorkflowWorkspace.RelativeFolderFor("Weather", workflowId)}/{WorkflowWorkspace.MetadataFileName}", clone.Snapshot.Files.Keys);
        Assert.Equal(new WorkflowWorkspaceIdentity("Weather"), await _host.ReadWorkflowIdentityAsync(clone.Snapshot, CancellationToken.None));
    }

    [Fact]
    public async Task PushWorkflowAsync_OfANameChangedOnlyInTheMetadata_RenamesTheFlowAndALaterCloneTakesTheNewName()
    {
        var workflowId = Guid.NewGuid();
        var scaffold = Scaffold("Weather", workflowId);
        var metadataPath = $"Weather/{WorkflowWorkspace.RelativeFolderFor("Weather", workflowId)}/{WorkflowWorkspace.MetadataFileName}";
        await PushAsync(scaffold);
        var renamed = With(scaffold, metadataPath, Regex.Replace(Text(scaffold, metadataPath), "^name: Weather(?=\\r?$)", "name: Weather Renamed", RegexOptions.Multiline));

        Assert.Empty((await _host.PushWorkflowAsync(renamed, Target(workflowId), HostedCloud.Options(), _authentication, CancellationToken.None)).Diagnostics);
        Assert.Equal("Weather Renamed", _cloud.StandaloneWorkflows.Stored(workflowId)!.Name);
        Assert.Equal(new WorkflowWorkspaceIdentity("Weather Renamed"), await _host.ReadWorkflowIdentityAsync(renamed, CancellationToken.None));
        var clone = await CloneAsync(workflowId);
        var writes = _cloud.StandaloneWorkflows.Calls.Count(call => call is "insert" or "update" or "delete");
        await _host.PushWorkflowAsync(clone.Snapshot, Target(workflowId), HostedCloud.Options(), _authentication, CancellationToken.None);

        Assert.Contains($"Weather/{WorkflowWorkspace.RelativeFolderFor("Weather Renamed", workflowId)}/{WorkflowWorkspace.MetadataFileName}", clone.Snapshot.Files.Keys);
        Assert.Equal(writes, _cloud.StandaloneWorkflows.Calls.Count(call => call is "insert" or "update" or "delete"));
    }

    [Fact]
    public async Task ValidateWorkflowAsync_OfAScaffoldedFolder_IsValidAndTellsItsWorkflowWithoutTouchingAnyEnvironment()
    {
        var workflowId = Guid.NewGuid();

        var validation = await _host.ValidateWorkflowAsync(Scaffold("Weather", workflowId), CancellationToken.None);

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Diagnostics);
        Assert.Equal(workflowId.ToString(), validation.WorkflowId);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_cloud.Cloud.Client.Invocations);
        Assert.Empty(_authentication.Audiences);
        Assert.Empty(_files.Opened);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task ValidateWorkflowAsync_OfAFolderNotHoldingExactlyOneWorkflow_ReportsWhatAPushRefuses(int workflowCount)
    {
        var snapshot = Enumerable.Range(1, workflowCount).Aggregate(WithoutWorkflows(Scaffold("Weather", Guid.NewGuid())), (current, index) => WithWorkflow(current, $"Flow{index}", Guid.NewGuid(), "{}"));

        var validation = await _host.ValidateWorkflowAsync(snapshot, CancellationToken.None);
        var refusal = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => PushAsync(snapshot));

        Assert.False(validation.IsValid);
        Assert.Null(validation.WorkflowId);
        Assert.Equal((AgentSyncDiagnosticCodes.WorkflowProjectInvalid, "Weather/"), (Assert.Single(validation.Diagnostics).Code, validation.Diagnostics[0].Path));
        Assert.Equal((AgentWorkspaceHostErrorCodes.SnapshotInvalid, "The snapshot of 'Weather' cannot be pushed."), (refusal.Code, refusal.Message));
        Assert.Equal(validation.Diagnostics, refusal.Diagnostics);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_files.Opened);
    }

    [Theory]
    [InlineData("settings.mcs.yml")]
    [InlineData("Agent.MCS.yaml")]
    public async Task ValidateWorkflowAsync_OfAFolderHoldingAnAgentBesideItsOneWorkflow_ReportsWhatReadsAndPushesRefuse(string agentFile)
    {
        var snapshot = With(Scaffold("Weather", Guid.NewGuid()), $"Weather/{agentFile}", "kind: GptComponentMetadata\n");

        var validation = await _host.ValidateWorkflowAsync(snapshot, CancellationToken.None);
        var readRefusal = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => _host.ReadWorkflowIdentityAsync(snapshot, CancellationToken.None));
        var pushRefusal = await Assert.ThrowsAsync<AgentWorkspaceHostException>(() => PushAsync(snapshot));

        Assert.False(validation.IsValid);
        Assert.Null(validation.WorkflowId);
        Assert.Equal((AgentSyncDiagnosticCodes.WorkflowProjectInvalid, "A standalone workflow folder holds no agent, and 'Weather' holds one.", "Weather/"), (Assert.Single(validation.Diagnostics).Code, validation.Diagnostics[0].Message, validation.Diagnostics[0].Path));
        Assert.Equal((AgentWorkspaceHostErrorCodes.SnapshotInvalid, AgentWorkspaceHostErrorCodes.SnapshotInvalid), (readRefusal.Code, pushRefusal.Code));
        Assert.Equal(validation.Diagnostics, readRefusal.Diagnostics);
        Assert.Equal(validation.Diagnostics, pushRefusal.Diagnostics);
        Assert.Empty(_cloud.StandaloneWorkflows.Calls);
        Assert.Empty(_files.Opened);
    }

    [Theory]
    [InlineData("Other/readme.md", "Other/")]
    [InlineData("readme.md", "readme.md")]
    public async Task ValidateWorkflowAsync_WithAFileOutsideTheFolder_ReportsItAndStillTellsTheWorkflow(string path, string reportedPath)
    {
        var workflowId = Guid.NewGuid();

        var validation = await _host.ValidateWorkflowAsync(With(Scaffold("Weather", workflowId), path, "{}"), CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.Equal((AgentSyncDiagnosticCodes.OutsideDeclaredFolders, reportedPath), (Assert.Single(validation.Diagnostics).Code, validation.Diagnostics[0].Path));
        Assert.Equal(workflowId.ToString(), validation.WorkflowId);
    }

    [Fact]
    public async Task ValidateWorkflowAsync_OfAnEmptySnapshot_ReportsItAndTellsNoWorkflow()
    {
        var validation = await _host.ValidateWorkflowAsync(new AgentWorkspaceSnapshot("Weather", new Dictionary<string, ReadOnlyMemory<byte>>()), CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.Equal(AgentSyncDiagnosticCodes.EmptySnapshot, Assert.Single(validation.Diagnostics).Code);
        Assert.Null(validation.WorkflowId);
    }

    [Theory]
    [InlineData("Weather/Nested")]
    [InlineData("Weather\\Nested")]
    [InlineData("..")]
    [InlineData(".mcs")]
    [InlineData(" ")]
    public async Task ValidateWorkflowAsync_OfASnapshotWhoseFolderIsNotATopLevelFolder_ReportsItAndTellsNoWorkflow(string folder)
    {
        var validation = await _host.ValidateWorkflowAsync(Scaffold("Weather", Guid.NewGuid()) with { Folder = folder }, CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.Equal(AgentSyncDiagnosticCodes.FolderInvalid, Assert.Single(validation.Diagnostics).Code);
        Assert.Null(validation.WorkflowId);
    }

    [Theory]
    [InlineData("Weather")]
    [InlineData("Météo \"Daily\" <Ops> & Co: #1")]
    public async Task CloneWorkflowAsync_OfAScaffoldItsPushInserted_WritesEveryFileTheScaffoldWroteByteForByte(string name)
    {
        var workflowId = Guid.NewGuid();
        var scaffold = Scaffolds.Workflow("Weather", name, workflowId);

        await PushAsync(scaffold);

        AgentWorkspaceHostCloneTests.AssertSameFiles(scaffold, (await CloneAsync(workflowId)).Snapshot);
    }

    internal static AgentWorkspaceSnapshot Scaffold(string name, Guid workflowId)
    {
        var workspace = new Microsoft.CopilotStudio.McsCore.InMemoryFileAccessor();
        WorkflowScaffolder.Scaffold(workspace, name, workflowId);

        return WorkspaceSnapshotFiles.Read(name, new[] { new KeyValuePair<string, IFileAccessor>(name, workspace) });
    }

    internal static WorkflowMetadata Row(Guid workflowId) => new() { WorkflowId = workflowId, Name = "Weather", ClientData = CloudDefinition, StateCode = 1, StatusCode = 2 };

    private static AgentWorkspaceSnapshot WithoutWorkflows(AgentWorkspaceSnapshot snapshot) => new(snapshot.Folder, snapshot.Files.Where(file => !file.Key.StartsWith($"{snapshot.Folder}/{WorkflowWorkspace.WorkflowsFolderName}/", StringComparison.Ordinal)).ToDictionary(file => file.Key, file => file.Value));

    private static WorkflowSyncTarget Target(Guid? workflowId = null) => new(HostedCloud.Organization, HostedCloud.EnvironmentId, HostedCloud.TenantId, workflowId?.ToString());

    private Task<WorkflowPushResult> PushAsync(AgentWorkspaceSnapshot snapshot, AgentPushOptions? options = null) => _host.PushWorkflowAsync(snapshot, Target(), options ?? HostedCloud.Options(), _authentication, CancellationToken.None);

    private Task<WorkflowCloneResult> CloneAsync(Guid workflowId) => _host.CloneWorkflowAsync(new WorkflowCloneRequest(Target(workflowId), "Weather"), _authentication, CancellationToken.None);
}
