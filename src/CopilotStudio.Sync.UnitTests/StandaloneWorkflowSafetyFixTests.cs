// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowSafetyFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcs-safety-fix-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private const string DefinitionWithReference = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
    """;

    private static readonly Encoding NoByteOrderMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly StandaloneWorkflowUpsertOptions AllowingReplace = new() { AllowDestructiveReplace = true };

    private static readonly StandaloneWorkflowUpsertOptions Retargeting = new() { ActivationMode = WorkflowActivationMode.DraftWhenConnectionReferencesExist };

    private static InMemoryFileAccessor NewWorkspace() => new InMemoryFileAccessor(new DirectoryPath("workspace"));

    private static void Write(IFileAccessor workspace, string path, string content)
    {
        using var stream = workspace.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream, NoByteOrderMark);

        writer.Write(content);
    }

    private static string? Read(IFileAccessor workspace, string path)
    {
        var located = new AgentFilePath(path);

        if (!workspace.Exists(located))
        {
            return null;
        }

        using var stream = workspace.OpenRead(located);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return reader.ReadToEnd();
    }

    private static InMemoryFileAccessor WorkspaceWith(Guid workflowId, string name, string definition, int stateCode = 0)
    {
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor(name, workflowId);

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", definition);
        Write(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new SyncDataverseClient.WorkflowMetadata
        {
            WorkflowId = workflowId,
            Name = name,
            StateCode = stateCode,
            StatusCode = stateCode == 1 ? 2 : 1,
            JsonFileName = $"{folder}/{WorkflowWorkspace.DefinitionFileName}",
        }));

        return workspace;
    }

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string name, string definition, int stateCode = 0) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = definition,
        StateCode = stateCode,
        StatusCode = stateCode == 1 ? 2 : 1,
    };

    [Fact]
    public async Task ARefusedUpdate_DoesNotDeleteTheCloudWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { UpdateFailure = "ConnectionAuthorizationFailed" };

        client.Seed(Row(workflowId, "Flow", """{"properties":{"a":1}}"""));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.ConnectionsNotReady, failure.Failure);
        Assert.DoesNotContain("delete", client.Calls);
        Assert.Equal("""{"properties":{"a":1}}""", (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData);
    }

    [Fact]
    public async Task ARefusedUpdate_RestartsAWorkflowItStoppedBeforeFailing()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.UpdateFailures.Enqueue("ConnectionAuthorizationFailed");
        client.UpdateFailures.Enqueue("ConnectionAuthorizationFailed");
        client.Seed(Row(workflowId, "Flow", """{"properties":{"a":1}}""", stateCode: 1));

        await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None));

        Assert.DoesNotContain("delete", client.Calls);
        Assert.Contains("start", client.Calls);
        Assert.Equal(1, (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).StateCode);
    }

    [Fact]
    public async Task ARefusedUpdate_ReplacesTheRowOnlyWhenTheCallerAsksForIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { UpdateFailure = "ConnectionAuthorizationFailed" };

        client.Seed(Row(workflowId, "Flow", "{}"));

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None, AllowingReplace);

        Assert.True(result.Replaced);
        Assert.Contains("delete", client.Calls);
    }

    [Fact]
    public async Task ACreateIsUnaffectedByTheReplacementGuard()
    {
        var client = new FakeStandaloneWorkflowClient();

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(Guid.NewGuid(), "Flow", DefinitionWithReference), "env", CancellationToken.None);

        Assert.True(result.Created);
        Assert.False(result.Replaced);
    }

    [Fact]
    public async Task AnUnboundActivationRequest_ExplainsThatTheWorkflowStayedADraft()
    {
        var client = new FakeStandaloneWorkflowClient();

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(Guid.NewGuid(), "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None);

        Assert.True(result.ActivationRequested);
        Assert.False(result.Workflow.IsActivated);
        Assert.Contains("draft", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connection", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnActivationRequestThatLands_StillReportsNoWarning()
    {
        var client = new FakeStandaloneWorkflowClient();

        client.ConnectionReferences["new_teams"] = "a-connection";

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(Guid.NewGuid(), "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None);

        Assert.True(result.Workflow.IsActivated);
        Assert.Equal(string.Empty, result.Warning);
    }

    [Fact]
    public async Task Retargeting_KeepsAConnectionReferenceWorkflowDraftedEvenWhenEveryReferenceIsBound()
    {
        var client = new FakeStandaloneWorkflowClient();

        client.ConnectionReferences["new_teams"] = "a-connection";

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(Guid.NewGuid(), "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None, Retargeting);

        Assert.DoesNotContain("start", client.Calls);
        Assert.False(result.Workflow.IsActivated);
        Assert.Contains("draft", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Retargeting_DraftsARunningCloudWorkflowThatBindsAConnectionReference()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.ConnectionReferences["new_teams"] = "a-connection";
        client.Seed(Row(workflowId, "Flow", DefinitionWithReference, stateCode: 1));

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None, Retargeting);

        Assert.Contains("stop", client.Calls);
        Assert.Equal(0, (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).StateCode);
        Assert.False(result.Workflow.IsActivated);
    }

    [Fact]
    public async Task Retargeting_LeavesAWorkflowWithoutConnectionReferencesAlone()
    {
        var client = new FakeStandaloneWorkflowClient();

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(Guid.NewGuid(), "Flow", """{"properties":{"a":1}}""", stateCode: 1), "env", CancellationToken.None, Retargeting);

        Assert.Contains("start", client.Calls);
        Assert.True(result.Workflow.IsActivated);
        Assert.Equal(string.Empty, result.Warning);
    }

    [Fact]
    public async Task ARefusedStop_WarnsWithoutClaimingTheWorkflowOnlyLandedAsADraft()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { StopFailure = """{"error":{"code":"0x80040217","message":"The workflow cannot be stopped."}}""" };

        client.Seed(Row(workflowId, "Flow", """{"properties":{"a":1}}""", stateCode: 1));

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(
            WorkspaceWith(workflowId, "Flow", """{"properties":{"a":1}}""", stateCode: 0),
            "env",
            CancellationToken.None);

        Assert.NotEqual(string.Empty, result.Warning);
        Assert.False(result.DraftedForConnections);
    }

    [Fact]
    public async Task AWorkflowDraftedForUnboundConnections_SaysSoStructurally()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(
            WorkspaceWith(workflowId, "Flow", DefinitionWithReference, stateCode: 1),
            "env",
            CancellationToken.None);

        Assert.NotEqual(string.Empty, result.Warning);
        Assert.True(result.DraftedForConnections);
    }

    [Fact]
    public async Task AWorkflowThatLandedRunning_ReportsNeitherAWarningNorDrafting()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(
            WorkspaceWith(workflowId, "Flow", """{"properties":{"a":1}}""", stateCode: 1),
            "env",
            CancellationToken.None);

        Assert.Equal(string.Empty, result.Warning);
        Assert.False(result.DraftedForConnections);
    }

    [Fact]
    public void TheWorkspaceStoreTheseRenameTestsUse_IsCaseSensitive()
    {
        var workspace = NewWorkspace();

        Write(workspace, "workflows/Flow/workflow.json", "{}");

        Assert.True(workspace.Exists(new AgentFilePath("workflows/Flow/workflow.json")));
        Assert.False(workspace.Exists(new AgentFilePath("workflows/flow/workflow.json")));
    }

    [Fact]
    public async Task ACaseOnlyRename_OnCaseInsensitiveStorage_KeepsOneWorkflowReadableUnderEitherSpelling()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = new McsCore.InMemoryFileAccessorFactory().Create(new DirectoryPath("workspace"));

        Assert.True(CaseInsensitive(workspace));

        client.Seed(Row(workflowId, "Flow", """{"properties":{"a":1}}"""));

        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        var renamed = (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        renamed.Name = "flow";
        renamed.ClientData = """{"properties":{"a":2}}""";
        client.Seed(renamed);

        var download = await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None, new StandaloneWorkflowDownloadOptions { OverwriteLocalChanges = true });

        Assert.Equal(WorkflowWorkspace.RelativeFolderFor("flow", workflowId), download.Folder);
        Assert.Equal(workflowId, Assert.Single(WorkflowWorkspace.FindAll(workspace)).WorkflowId);
        Assert.Contains("\"a\": 2", (Read(workspace, $"{download.Folder}/{WorkflowWorkspace.DefinitionFileName}") ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")), StringComparison.Ordinal);
        Assert.Equal(
            Read(workspace, $"{download.Folder}/{WorkflowWorkspace.DefinitionFileName}"),
            Read(workspace, $"{WorkflowWorkspace.RelativeFolderFor("Flow", workflowId)}/{WorkflowWorkspace.DefinitionFileName}"));
    }

    private static bool CaseInsensitive(IFileAccessor workspace)
    {
        Write(workspace, "probe/Case.txt", "{}");

        return workspace.Exists(new AgentFilePath("probe/case.txt"));
    }

    [Fact]
    public async Task ACaseOnlyRename_LeavesOneFolderHoldingTheWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();

        client.Seed(Row(workflowId, "Flow", """{"properties":{"a":1}}"""));

        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        var renamed = (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        renamed.Name = "flow";
        client.Seed(renamed);

        var download = await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.Equal(WorkflowWorkspace.RelativeFolderFor("flow", workflowId), download.Folder);
        Assert.Equal(workflowId, Assert.Single(WorkflowWorkspace.FindAll(workspace)).WorkflowId);
        Assert.NotNull(Read(workspace, $"{download.Folder}/{WorkflowWorkspace.DefinitionFileName}"));
        Assert.Null(Read(workspace, $"{WorkflowWorkspace.RelativeFolderFor("Flow", workflowId)}/{WorkflowWorkspace.DefinitionFileName}"));
    }

    [Fact]
    public async Task ARenameStillMovesTheFolder()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();

        client.Seed(Row(workflowId, "Flow", """{"properties":{"a":1}}"""));

        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        var renamed = (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        renamed.Name = "Other";
        client.Seed(renamed);

        var download = await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.Equal(WorkflowWorkspace.RelativeFolderFor("Other", workflowId), download.Folder);
        Assert.Single(WorkflowWorkspace.FindAll(workspace));
        Assert.Null(Read(workspace, $"{WorkflowWorkspace.RelativeFolderFor("Flow", workflowId)}/{WorkflowWorkspace.DefinitionFileName}"));
    }

    [Fact]
    public async Task ACaseOnlyRename_MergesAFolderThatAlreadySitsUnderTheNewName()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();

        client.Seed(Row(workflowId, "flow", """{"properties":{"a":1}}"""));

        var stale = WorkflowWorkspace.RelativeFolderFor("Flow", workflowId);

        Write(workspace, $"{stale}/{WorkflowWorkspace.DefinitionFileName}", "{}");
        Write(workspace, $"{stale}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "Flow" }));

        var download = await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None, new StandaloneWorkflowDownloadOptions { OverwriteLocalChanges = true });

        Assert.Equal(WorkflowWorkspace.RelativeFolderFor("flow", workflowId), download.Folder);
        Assert.Single(WorkflowWorkspace.FindAll(workspace));
        Assert.Null(Read(workspace, $"{stale}/{WorkflowWorkspace.DefinitionFileName}"));
    }

    [Fact]
    public async Task ACaseOnlyRename_OnTheRealFileSystem_KeepsOneReadableWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = WorkflowWorkspace.OpenWorkspace(_root);

        Directory.CreateDirectory(_root);
        client.Seed(Row(workflowId, "Flow", """{"properties":{"a":1}}"""));

        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        var renamed = (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        renamed.Name = "flow";
        renamed.ClientData = """{"properties":{"a":2}}""";
        client.Seed(renamed);

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        var found = Assert.Single(WorkflowWorkspace.FindAll(_root));

        Assert.Equal(workflowId, found.WorkflowId);
        Assert.Contains("\"a\": 2", File.ReadAllText(found.DefinitionPath), StringComparison.Ordinal);
        Assert.Equal(workflowId, (await synchronizer.ReadLocalAsync(workspace, CancellationToken.None)).WorkflowId);
    }
}
