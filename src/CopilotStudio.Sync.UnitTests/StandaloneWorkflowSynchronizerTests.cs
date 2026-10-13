// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

internal sealed class FakeStandaloneWorkflowClient : IStandaloneWorkflowDataverseClient
{
    private readonly Dictionary<Guid, SyncDataverseClient.WorkflowMetadata> _rows = new();

    public List<string> Calls { get; } = new();

    public Dictionary<string, string?> ConnectionReferences { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? InsertFailure { get; set; }

    public string? UpdateFailure { get; set; }

    public Queue<string> UpdateFailures { get; } = new();

    public string? DeleteFailure { get; set; }

    public string? StartFailure { get; set; }

    public string? RestoreFailure { get; set; }

    public Func<SyncDataverseClient.WorkflowMetadata, SyncDataverseClient.WorkflowMetadata>? NormalizeOnWrite { get; set; }

    public bool ReturnStoredInstance { get; set; }

    public int GetCount => Calls.Count(call => call == "get");

    public void Seed(SyncDataverseClient.WorkflowMetadata row) => _rows[row.WorkflowId] = Copy(row);

    public SyncDataverseClient.WorkflowMetadata? Stored(Guid workflowId) => _rows.TryGetValue(workflowId, out var row) ? Copy(row) : null;

    public void SetDataverseUrl(string dataverseUrl) => Calls.Add("target");

    public Task<WorkflowResponse> InsertWorkflowAsync(SyncDataverseClient.WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
    {
        Calls.Add("insert");

        if (_rows.ContainsKey(workflowMetadata!.WorkflowId) && RestoreFailure is not null)
        {
            return Task.FromResult(new WorkflowResponse { ErrorMessage = RestoreFailure });
        }

        if (InsertFailure is { } failure && !_rows.ContainsKey(workflowMetadata.WorkflowId))
        {
            return Task.FromResult(new WorkflowResponse { ErrorMessage = failure });
        }

        _rows[workflowMetadata.WorkflowId] = Normalize(workflowMetadata);

        return Task.FromResult(new WorkflowResponse { WorkflowId = workflowMetadata.WorkflowId });
    }

    public Task<SyncDataverseClient.WorkflowMetadata?> GetWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        Calls.Add("get");

        return Task.FromResult(_rows.TryGetValue(workflowId, out var row) ? (ReturnStoredInstance ? row : Copy(row)) : null);
    }

    public Task<SyncDataverseClient.WorkflowSummary[]> ListWorkflowsAsync(string? nameFilter, int? maximumCount, CancellationToken cancellationToken)
    {
        Calls.Add("list");

        var rows = _rows.Values
            .Where(row => nameFilter is null || (row.Name?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(row => new SyncDataverseClient.WorkflowSummary { WorkflowId = row.WorkflowId, Name = row.Name, Description = row.Description, StateCode = row.StateCode });

        return Task.FromResult((maximumCount is { } cap ? rows.Take(cap) : rows).ToArray());
    }

    public Task<bool> DeleteWorkflowAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        Calls.Add("delete");

        if (DeleteFailure is { } refusal)
        {
            throw new DataverseRequestException(System.Net.HttpStatusCode.BadRequest, refusal);
        }

        return Task.FromResult(_rows.Remove(workflowId));
    }

    public Task<WorkflowResponse> UpdateWorkflowDefinitionAsync(SyncDataverseClient.WorkflowMetadata? workflowMetadata, CancellationToken cancellationToken)
    {
        Calls.Add("update");

        if ((UpdateFailures.Count > 0 ? UpdateFailures.Dequeue() : UpdateFailure) is { } failure)
        {
            return Task.FromResult(new WorkflowResponse { ErrorMessage = failure });
        }

        var stored = Normalize(workflowMetadata!);

        if (workflowMetadata!.StateCode is null && _rows.TryGetValue(workflowMetadata.WorkflowId, out var previous))
        {
            stored.StateCode = previous.StateCode;
            stored.StatusCode = previous.StatusCode;
        }

        _rows[workflowMetadata.WorkflowId] = stored;

        return Task.FromResult(new WorkflowResponse { WorkflowId = workflowMetadata.WorkflowId });
    }

    public Task SetWorkflowStateAsync(Guid workflowId, bool activate, CancellationToken cancellationToken)
    {
        Calls.Add(activate ? "start" : "stop");

        if (activate && StartFailure is { } refusal)
        {
            throw new DataverseRequestException(System.Net.HttpStatusCode.BadRequest, refusal);
        }

        if (_rows.TryGetValue(workflowId, out var row))
        {
            row.StateCode = activate ? 1 : 0;
            row.StatusCode = activate ? 2 : 1;
        }

        return Task.CompletedTask;
    }

    public Task EnsureConnectionReferenceExistsAsync(string connectionReferenceLogicalName, string connectorId, CancellationToken cancellationToken, Guid? customConnectorRowId = null)
    {
        Calls.Add("declare:" + connectionReferenceLogicalName);
        ConnectionReferences[connectionReferenceLogicalName] = null;

        return Task.CompletedTask;
    }

    public Task<SyncDataverseClient.ConnectionReferenceInfo[]> GetConnectionReferencesByLogicalNamesAsync(IEnumerable<string> logicalNames, CancellationToken cancellationToken)
    {
        Calls.Add("references");

        return Task.FromResult(logicalNames
            .Where(ConnectionReferences.ContainsKey)
            .Select(name => new SyncDataverseClient.ConnectionReferenceInfo { ConnectionReferenceLogicalName = name, ConnectionId = ConnectionReferences[name] })
            .ToArray());
    }

    private SyncDataverseClient.WorkflowMetadata Normalize(SyncDataverseClient.WorkflowMetadata metadata) =>
        NormalizeOnWrite is null ? Copy(metadata) : NormalizeOnWrite(Copy(metadata));

    private static SyncDataverseClient.WorkflowMetadata Copy(SyncDataverseClient.WorkflowMetadata source) => new()
    {
        WorkflowId = source.WorkflowId,
        Name = source.Name,
        Description = source.Description,
        ClientData = source.ClientData,
        StateCode = source.StateCode,
        StatusCode = source.StatusCode,
        JsonFileName = source.JsonFileName,
        ConnectionReferences = source.ConnectionReferences.ToList(),
    };
}

public class StandaloneWorkflowSynchronizerTests
{
    private const string DefinitionWithReference = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}}}}
    """;

    private static readonly Encoding NoByteOrderMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
    public async Task Download_WritesTheDefinitionMetadataAndDeclaration()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();

        client.Seed(Row(workflowId, "My Flow", DefinitionWithReference));

        var result = await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.True(result.Changed);
        Assert.NotNull(Read(workspace, $"{result.Folder}/{WorkflowWorkspace.DefinitionFileName}"));
        Assert.NotNull(Read(workspace, $"{result.Folder}/{WorkflowWorkspace.MetadataFileName}"));
        Assert.NotNull(Read(workspace, $"{ConnectionReferenceStore.LayeredFolderName}/new_teams{ConnectionReferenceStore.LayeredFileExtension}"));
        Assert.NotNull(Read(workspace, AgentClassifier.WorkspaceLayoutMarkerFileName));
    }

    [Fact]
    public async Task Download_WhenNothingChanged_ReportsNoChange()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "My Flow", DefinitionWithReference));

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.False((await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None)).Changed);
    }

    [Fact]
    public async Task Download_LeavesTheServiceMetadataUntouched()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { ReturnStoredInstance = true };
        var workspace = NewWorkspace();
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "My Flow", DefinitionWithReference));

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.Null(client.Stored(workflowId)!.JsonFileName);
        Assert.False((await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None)).Changed);
    }

    [Fact]
    public async Task Download_WhenOnlyTheCloudMirrorCacheIsMissing_ReportsNoChangeAndRestoresIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "My Flow", DefinitionWithReference));

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.True(StandaloneWorkflowCache.Remove(workspace, workflowId));

        var downloaded = await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.False(downloaded.Changed);
        Assert.NotNull(StandaloneWorkflowCache.Read(workspace, workflowId));
    }

    [Fact]
    public async Task Download_WhenAnAuthoredFileWasEdited_ReportsAChange()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "My Flow", DefinitionWithReference));

        var first = await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Write(workspace, $"{first.Folder}/{WorkflowWorkspace.DefinitionFileName}", "{}");

        Assert.True((await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None, new StandaloneWorkflowDownloadOptions { OverwriteLocalChanges = true })).Changed);
    }

    [Fact]
    public async Task Download_WhenTheWorkflowWasRenamed_MovesTheFolder()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "Before", DefinitionWithReference));
        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        client.Seed(Row(workflowId, "After", DefinitionWithReference));
        var moved = await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.True(moved.Changed);
        Assert.Single(WorkflowWorkspace.FindAll(workspace));
        Assert.Equal(WorkflowWorkspace.RelativeFolderFor("After", workflowId), moved.Folder);
    }

    [Fact]
    public async Task Download_CachesTheSameDefinitionNameTheWorkspaceWrote()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("NB Workflow1", workflowId);

        client.Seed(Row(workflowId, "NB Workflow1", "{}"));

        await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        var cached = StandaloneWorkflowCache.Read(workspace, workflowId);
        var local = WorkflowMetadataFile.ReadStrict(Read(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}")!);

        Assert.Equal($"{folder}/{WorkflowWorkspace.DefinitionFileName}", local!.JsonFileName);
        Assert.Equal(local.JsonFileName, cached!.JsonFileName);
    }

    [Fact]
    public async Task Download_OfAWorkflowTheEnvironmentDoesNotHold_Fails()
    {
        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(new FakeStandaloneWorkflowClient()).DownloadAsync(Guid.NewGuid(), NewWorkspace(), "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.SourceNotFound, failure.Failure);
    }

    [Fact]
    public async Task ReadLocal_WhenTheFolderNamesADifferentWorkflow_Fails()
    {
        var workspace = WorkspaceWith(Guid.NewGuid(), "Flow", DefinitionWithReference);
        var renamed = WorkflowWorkspace.FindAll(workspace).Single();

        Write(workspace, renamed.MetadataPath.ToString(), WorkflowMetadataFile.Write(new SyncDataverseClient.WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Flow" }));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(new FakeStandaloneWorkflowClient()).ReadLocalAsync(workspace, CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.MetadataUnreadable, failure.Failure);
    }

    [Fact]
    public async Task ReadLocal_OfAWorkspaceHoldingNoWorkflow_Fails()
    {
        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(new FakeStandaloneWorkflowClient()).ReadLocalAsync(NewWorkspace(), CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.NotFound, failure.Failure);
    }

    [Fact]
    public async Task ReadLocal_OfAWorkspaceHoldingTwoWorkflows_Fails()
    {
        var workspace = WorkspaceWith(Guid.NewGuid(), "First", DefinitionWithReference);
        var second = Guid.NewGuid();

        Write(workspace, $"{WorkflowWorkspace.RelativeFolderFor("Second", second)}/{WorkflowWorkspace.DefinitionFileName}", DefinitionWithReference);
        Write(workspace, $"{WorkflowWorkspace.RelativeFolderFor("Second", second)}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new SyncDataverseClient.WorkflowMetadata { WorkflowId = second, Name = "Second" }));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(new FakeStandaloneWorkflowClient()).ReadLocalAsync(workspace, CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.Ambiguous, failure.Failure);
    }

    [Fact]
    public async Task Upsert_CreatesTheRowAndDeclaresItsConnectionReference()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal(["new_teams"], result.CreatedConnectionReferences);
        Assert.NotNull(client.Stored(workflowId));
    }

    [Fact]
    public async Task Upsert_WhenNothingDiffers_WritesNothing()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = WorkspaceWith(workflowId, "Flow", DefinitionWithReference);

        client.Seed(Row(workflowId, "Flow", WorkflowDefinitionFile.PortableDefinition(DefinitionWithReference)));

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(workspace, "env", CancellationToken.None);

        Assert.DoesNotContain("insert", client.Calls);
        Assert.DoesNotContain("update", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenTheEnvironmentRefusesOverAConnection_ReplacesTheRow()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { UpdateFailure = "ConnectionAuthorizationFailed" };

        client.Seed(Row(workflowId, "Flow", "{}"));

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None, new StandaloneWorkflowUpsertOptions { AllowDestructiveReplace = true });

        Assert.True(result.Replaced);
        Assert.Contains("delete", client.Calls);
        Assert.NotNull(client.Stored(workflowId));
    }

    [Fact]
    public async Task Upsert_WhenTheReplacementCannotWriteBack_ReportsTheLostCloudCopy()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { UpdateFailure = "ConnectionAuthorizationFailed", InsertFailure = "refused", RestoreFailure = "restore refused" };

        client.Seed(Row(workflowId, "Flow", "{}"));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None, new StandaloneWorkflowUpsertOptions { AllowDestructiveReplace = true }));

        Assert.Equal(StandaloneWorkflowFailure.ReplaceLostTheCloudCopy, failure.Failure);
    }

    [Fact]
    public async Task Upsert_WhenTheServiceRefusesTheWrite_ReportsThePushFailure()
    {
        var client = new FakeStandaloneWorkflowClient { InsertFailure = "quota exceeded" };

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(Guid.NewGuid(), "Flow", DefinitionWithReference), "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.PushFailed, failure.Failure);
    }

    [Fact]
    public async Task Upsert_WhenAWorkflowAsksToRunButItsReferenceIsUnbound_LandsAsADraft()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None);

        Assert.True(result.ActivationRequested);
        Assert.False(result.Workflow.IsActivated);
        Assert.DoesNotContain("start", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenEveryReferenceIsBound_StartsTheWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.ConnectionReferences["new_teams"] = "a-connection";

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None);

        Assert.Contains("start", client.Calls);
    }

    [Fact]
    public async Task Upsert_RestartsAWorkflowItStoppedWhenTheRetryAlsoFails()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.UpdateFailures.Enqueue("Dataverse request failed (400): ConnectionAuthorizationFailed");
        client.UpdateFailures.Enqueue("refused");
        client.Seed(Row(workflowId, "Flow", "{}", stateCode: 1));

        await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None));

        Assert.Contains("stop", client.Calls);
        Assert.Contains("start", client.Calls);
    }

    [Fact]
    public async Task Upsert_WhenTheWriteFails_LeavesARunningWorkflowUntouched()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { UpdateFailure = "refused" };

        client.Seed(Row(workflowId, "Flow", "{}", stateCode: 1));

        await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None));

        Assert.DoesNotContain("stop", client.Calls);
        Assert.DoesNotContain("start", client.Calls);
    }

    [Fact]
    public async Task Upsert_UpdatesARunningWorkflowWithoutStoppingOrStartingIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "Flow", "{}", stateCode: 1));

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None);

        Assert.Contains("update", client.Calls);
        Assert.DoesNotContain("stop", client.Calls);
        Assert.DoesNotContain("start", client.Calls);
    }

    [Fact]
    public async Task Upsert_AsksTheEnvironmentForItsConnectionReferencesOnce()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "Flow", "{}", stateCode: 1));

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None);

        Assert.Equal(1, client.Calls.Count(call => call == "references"));
    }

    [Fact]
    public async Task Upsert_WhenTheEnvironmentRefusesOverAConnection_DraftsTheWorkflowAndKeepsTheContent()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.UpdateFailures.Enqueue("Dataverse request failed (400): ConnectionAuthorizationFailed");
        client.Seed(Row(workflowId, "Flow", "{}", stateCode: 1));

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None);

        Assert.False(result.Replaced);
        Assert.DoesNotContain("delete", client.Calls);
        Assert.Contains("stop", client.Calls);
        Assert.Contains("draft", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkflowDefinitionFile.Read(DefinitionWithReference).Portable, client.Stored(workflowId)!.ClientData);
    }

    [Fact]
    public async Task Upsert_WhenTheEnvironmentRefusesToDeleteTheRow_ReportsConnectionsNotReady()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient
        {
            UpdateFailure = "Connection 'new_teams' cannot be used to activate this flow",
            DeleteFailure = "Connection 'new_teams' cannot be used to activate this flow",
        };

        client.Seed(Row(workflowId, "Flow", "{}"));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference), "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.ConnectionsNotReady, failure.Failure);
        Assert.NotNull(client.Stored(workflowId));
    }

    [Fact]
    public async Task Upsert_WhenStartingIsRefusedOverAConnection_WarnsInsteadOfFailing()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { StartFailure = "ConnectionAuthorizationFailed" };

        client.ConnectionReferences["new_teams"] = "a-connection";

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", DefinitionWithReference, stateCode: 1), "env", CancellationToken.None);

        Assert.Contains("start", client.Calls);
        Assert.Contains("draft", result.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(client.Stored(workflowId));
    }

    [Fact]
    public async Task Upsert_WhenNothingGoesWrong_ReportsNoWarning()
    {
        var client = new FakeStandaloneWorkflowClient();

        var result = await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(Guid.NewGuid(), "Flow", DefinitionWithReference), "env", CancellationToken.None);

        Assert.Equal(string.Empty, result.Warning);
    }

    [Fact]
    public void RequireSingle_WhenTheWorkspaceHoldsOne_ReturnsIt()
    {
        var workflowId = Guid.NewGuid();
        var workspace = WorkspaceWith(workflowId, "Only", DefinitionWithReference);

        Assert.Equal(workflowId, WorkflowWorkspace.RequireSingle(workspace, "subject").WorkflowId);
    }

    [Fact]
    public void RequireSingle_WhenTheWorkspaceHoldsNone_NamesTheSubject()
    {
        var failure = Assert.Throws<StandaloneWorkflowException>(() => WorkflowWorkspace.RequireSingle(NewWorkspace(), "c:/projects/flow"));

        Assert.Equal(StandaloneWorkflowFailure.NotFound, failure.Failure);
        Assert.Equal("c:/projects/flow", failure.Subject);
    }

    [Fact]
    public void RequireSingle_WhenTheWorkspaceHoldsSeveral_NamesEveryFolder()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var workspace = WorkspaceWith(first, "First", DefinitionWithReference);

        Write(workspace, $"{WorkflowWorkspace.RelativeFolderFor("Second", second)}/{WorkflowWorkspace.DefinitionFileName}", DefinitionWithReference);
        Write(workspace, $"{WorkflowWorkspace.RelativeFolderFor("Second", second)}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new SyncDataverseClient.WorkflowMetadata { WorkflowId = second, Name = "Second" }));

        var failure = Assert.Throws<StandaloneWorkflowException>(() => WorkflowWorkspace.RequireSingle(workspace, "subject"));

        Assert.Equal(StandaloneWorkflowFailure.Ambiguous, failure.Failure);
        Assert.Equal(2, failure.FolderNames.Count);
    }

    [Fact]
    public async Task List_ReturnsTheEnvironmentsFlowsInNameOrder()
    {
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(Guid.NewGuid(), "Zulu", "{}"));
        client.Seed(Row(Guid.NewGuid(), "Alpha", "{}"));

        Assert.Equal(["Alpha", "Zulu"], (await new StandaloneWorkflowSynchronizer(client).ListAsync(null, null, CancellationToken.None)).Select(workflow => workflow.Name));
    }

    [Fact]
    public async Task List_AppliesTheNameFilterAndTheCap()
    {
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(Guid.NewGuid(), "Weather Flow", "{}"));
        client.Seed(Row(Guid.NewGuid(), "Teams Flow", "{}"));
        client.Seed(Row(Guid.NewGuid(), "Unrelated", "{}"));

        Assert.Equal(2, (await new StandaloneWorkflowSynchronizer(client).ListAsync("Flow", null, CancellationToken.None)).Count);
        Assert.Single(await new StandaloneWorkflowSynchronizer(client).ListAsync(null, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_ReportsWhetherTheRowWasThere()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "Flow", "{}"));

        Assert.True(await synchronizer.DeleteAsync(workflowId, null, CancellationToken.None));
        Assert.False(await synchronizer.DeleteAsync(workflowId, null, CancellationToken.None));
    }
}

public class StandaloneWorkflowCloudCacheTests
{
    private const string Definition = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"},"connectionName":"bound-in-the-environment"}}}}
    """;

    private static readonly Encoding NoByteOrderMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static InMemoryFileAccessor NewWorkspace() => new InMemoryFileAccessor(new DirectoryPath("workspace"));

    private static void Write(IFileAccessor workspace, string path, string content)
    {
        using var stream = workspace.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream, NoByteOrderMark);

        writer.Write(content);
    }

    private static InMemoryFileAccessor WorkspaceWith(Guid workflowId, string name, string definition)
    {
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor(name, workflowId);

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", definition);
        Write(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = name, StateCode = 0, StatusCode = 1 }));

        return workspace;
    }

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string name, string definition) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = definition,
        StateCode = 0,
        StatusCode = 1,
    };

    [Fact]
    public async Task AfterAPush_TheCacheHoldsWhatTheServiceReturnedNotWhatWasSent()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient
        {
            NormalizeOnWrite = stored =>
            {
                stored.ClientData = stored.ClientData?.Replace("\"new_teams\"", "\"new_teams\"") + " ";

                return stored;
            },
        };

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(WorkspaceWith(workflowId, "Flow", Definition), "env", CancellationToken.None);

        var workspace = WorkspaceWith(workflowId, "Flow", Definition);

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(workspace, "env", CancellationToken.None);

        Assert.Equal(client.Stored(workflowId)!.ClientData, StandaloneWorkflowCache.Read(workspace, workflowId)!.ClientData);
    }

    [Fact]
    public async Task AfterAPush_TheCacheNeverHoldsThePortableFormTheWorkspaceSent()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = WorkspaceWith(workflowId, "Flow", Definition);

        client.Seed(Row(workflowId, "Flow", Definition));

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(workspace, "env", CancellationToken.None);

        Assert.Contains("connectionName", StandaloneWorkflowCache.Read(workspace, workflowId)!.ClientData!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCachedRawDefinition_StillComparesEqualToThePortableLocalCopy()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = WorkspaceWith(workflowId, "Flow", Definition);

        client.Seed(Row(workflowId, "Flow", Definition));

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(workspace, "env", CancellationToken.None);

        Assert.False(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    public async Task AnEditedDefinition_IsReportedAsALocalChange()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = WorkspaceWith(workflowId, "Flow", Definition);

        client.Seed(Row(workflowId, "Flow", Definition));

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(workspace, "env", CancellationToken.None);

        Write(workspace, WorkflowWorkspace.FindAll(workspace).Single().DefinitionPath.ToString(), """{"properties":{"edited":true}}""");

        Assert.True(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    public async Task AnUnchangedPush_SpendsNoExtraReadToRefreshTheCache()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = WorkspaceWith(workflowId, "Flow", Definition);

        client.Seed(Row(workflowId, "Flow", WorkflowDefinitionFile.PortableDefinition(Definition)));

        await new StandaloneWorkflowSynchronizer(client).UpsertAsync(workspace, "env", CancellationToken.None);

        Assert.Equal(1, client.GetCount);
    }

    [Fact]
    public async Task AFailedPush_LeavesTheCacheUntouched()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { InsertFailure = "refused" };
        var workspace = WorkspaceWith(workflowId, "Flow", Definition);

        await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).UpsertAsync(workspace, "env", CancellationToken.None));

        Assert.Null(StandaloneWorkflowCache.Read(workspace, workflowId));
    }

    [Fact]
    public async Task ADownload_CachesWhatTheServiceReturned()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();

        client.Seed(Row(workflowId, "Flow", Definition));

        await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.Equal(Definition, StandaloneWorkflowCache.Read(workspace, workflowId)!.ClientData);
    }

    [Fact]
    public async Task ADelete_DropsTheCachedCopy()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "Flow", Definition));

        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);
        await synchronizer.DeleteAsync(workflowId, workspace, CancellationToken.None);

        Assert.Null(StandaloneWorkflowCache.Read(workspace, workflowId));
    }

    [Fact]
    public void AWorkflowWithNoCachedCopy_CountsAsALocalChange()
    {
        var workflowId = Guid.NewGuid();
        var workspace = WorkspaceWith(workflowId, "Flow", Definition);

        Assert.True(StandaloneWorkflowCache.HasLocalChange(workspace, WorkflowWorkspace.FindAll(workspace).Single()));
    }

    [Fact]
    public async Task TheCacheLivesInTheAgentCacheFile()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();

        client.Seed(Row(workflowId, "Flow", Definition));

        await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.True(workspace.Exists(new AgentFilePath(".mcs/botdefinition.json")));
        Assert.False(workspace.Exists(new AgentFilePath($".mcs/workflows/{workflowId}/{WorkflowWorkspace.MetadataFileName}")));
    }
}
