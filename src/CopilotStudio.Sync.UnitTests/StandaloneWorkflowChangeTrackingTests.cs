// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowChangeTrackingTests : IDisposable
{
    private readonly string _root;
    private readonly DirectoryPath _workspace;
    private readonly Guid _workflowId = Guid.NewGuid();
    private readonly string _workflowFolder;

    public StandaloneWorkflowChangeTrackingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mcs-standalone-changes-" + Guid.NewGuid().ToString("N"));
        _workflowFolder = Path.Combine(_root, "workflows", "MyFlow-" + _workflowId.ToString("D"));
        Directory.CreateDirectory(_workflowFolder);
        File.WriteAllText(Path.Combine(_root, WorkflowWorkspace.ProjectFileName), WorkflowWorkspace.BuildProjectFile());
        _workspace = new DirectoryPath(_root.Replace('\\', '/') + "/");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static WorkspaceSynchronizer CreateSynchronizer() =>
        new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            new FileAccessorFactory(),
            new Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

    private string Metadata() =>
        $"workflowId: {_workflowId}\n" +
        "name: My Flow\n" +
        "type: 1\n" +
        "category: 5\n" +
        "stateCode: 1\n" +
        "statusCode: 2\n";

    private static string Definition(string triggerName) =>
        "{\n" +
        "  \"properties\": {\n" +
        $"    \"displayName\": \"{triggerName}\"\n" +
        "  }\n" +
        "}";

    private void WriteLocal(string definition)
    {
        File.WriteAllText(Path.Combine(_workflowFolder, "metadata.yml"), Metadata());
        File.WriteAllText(Path.Combine(_workflowFolder, "workflow.json"), definition);
    }

    private void WriteCache(string definition)
    {
        var metadata = WorkflowMetadataFile.ReadStrict(Metadata())!;
        metadata.ClientData = definition;

        StandaloneWorkflowCache.Write(Accessor(), metadata);
    }

    private IFileAccessor Accessor() => new FileAccessorFactory().Create(_workspace);

    private string? CachedDefinition() => StandaloneWorkflowCache.Read(Accessor(), _workflowId)?.ClientData;

    private string? CachedMetadata()
    {
        var cached = StandaloneWorkflowCache.Read(Accessor(), _workflowId);

        return cached is null ? null : WorkflowMetadataFile.Write(cached);
    }

    private Task<(PvaComponentChangeSet, System.Collections.Immutable.ImmutableArray<Change>)> GetLocalChangesAsync() =>
        CreateSynchronizer().GetLocalChangesAsync(_workspace, new BotDefinition(), CancellationToken.None);

    [Fact]
    public async Task StandaloneWorkspace_WhenTheCacheMatchesTheWorkspace_ReportsNoChange()
    {
        WriteLocal(Definition("Original"));
        WriteCache(Definition("Original"));

        var (_, changes) = await GetLocalChangesAsync();

        Assert.Empty(changes);
    }

    private const string BoundDefinition = """
    {"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams","connectionName":"shared-teams-abc123"}}}}}
    """;

    [Fact]
    public async Task StandaloneWorkspace_WhenOnlyTheEnvironmentBindingDiffers_ReportsNoChange()
    {
        WriteCache(BoundDefinition);
        WriteLocal(WorkflowDefinitionFile.Read(BoundDefinition).Portable);

        var (_, changes) = await GetLocalChangesAsync();

        Assert.Empty(changes);
    }

    [Fact]
    public async Task StandaloneWorkspace_ChangeTrackingAndTheCacheAgreeAboutPortableDefinitions()
    {
        WriteCache(BoundDefinition);
        WriteLocal(WorkflowDefinitionFile.Read(BoundDefinition).Portable);

        var (_, changes) = await GetLocalChangesAsync();
        var workflow = WorkflowWorkspace.FindAll(Accessor()).Single();

        Assert.Equal(StandaloneWorkflowCache.HasLocalChange(Accessor(), workflow), changes.Length > 0);
    }

    [Fact]
    public async Task StandaloneWorkspace_WhenThePortableDefinitionWasEdited_StillReportsAChange()
    {
        WriteCache(BoundDefinition);
        WriteLocal(WorkflowDefinitionFile.Read(BoundDefinition).Portable.Replace("shared_teams", "shared_office365", StringComparison.Ordinal));

        var (_, changes) = await GetLocalChangesAsync();
        var workflow = WorkflowWorkspace.FindAll(Accessor()).Single();

        Assert.NotEmpty(changes);
        Assert.True(StandaloneWorkflowCache.HasLocalChange(Accessor(), workflow));
    }

    [Fact]
    public async Task RemoteChanges_WhenOnlyTheEnvironmentBindingMoved_ReportsNoChange()
    {
        WriteCache(WorkflowDefinitionFile.Read(BoundDefinition).Portable);
        WriteLocal(WorkflowDefinitionFile.Read(BoundDefinition).Portable);

        var (_, changes) = await GetRemoteChangesAsync((_, _) => Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(RemoteRow(BoundDefinition)));

        Assert.Empty(changes);
    }

    [Fact]
    public async Task StandaloneWorkspace_WhenTheDefinitionWasEditedLocally_ReportsAChange()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Edited"));

        var (_, changes) = await GetLocalChangesAsync();

        var change = Assert.Single(changes);
        Assert.Equal(ChangeType.Update, change.ChangeType);
        Assert.Contains(_workflowId.ToString(), change.SchemaName);
    }

    [Fact]
    public async Task StandaloneWorkspace_WhenAWorkflowIsNotYetCached_ReportsACreate()
    {
        WriteLocal(Definition("Original"));

        var (_, changes) = await GetLocalChangesAsync();

        Assert.All(changes, change => Assert.Equal(ChangeType.Create, change.ChangeType));
        Assert.Equal(2, changes.Length);
    }

    [Fact]
    public async Task StandaloneWorkspace_WhenTheWorkspaceCopyWasDeleted_ReportsADelete()
    {
        WriteCache(Definition("Original"));

        var (_, changes) = await GetLocalChangesAsync();

        Assert.Equal(ChangeType.Delete, Assert.Single(changes).ChangeType);
    }

    private Task<(PvaComponentChangeSet, System.Collections.Immutable.ImmutableArray<Change>)> GetRemoteChangesAsync(Func<Guid, CancellationToken, Task<SyncDataverseClient.WorkflowMetadata?>> readWorkflow) =>
        CreateSynchronizer().GetStandaloneWorkflowRemoteChangesAsync(_workspace, readWorkflow, CancellationToken.None);

    private SyncDataverseClient.WorkflowMetadata RemoteRow(string definition) => new()
    {
        WorkflowId = _workflowId,
        Name = "My Flow",
        Type = 1,
        Category = 5,
        StateCode = 1,
        StatusCode = 2,
        ClientData = definition,
    };

    [Fact]
    public async Task RemoteChanges_WhenTheServiceMatchesTheCache_ReportsNoChange()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Original"));

        var (_, changes) = await GetRemoteChangesAsync((_, _) => Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(RemoteRow(Definition("Original"))));

        Assert.Empty(changes);
    }

    [Fact]
    public async Task RemoteChanges_WhenTheServiceMovedAhead_ReportsAChange()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Original"));

        var (_, changes) = await GetRemoteChangesAsync((_, _) => Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(RemoteRow(Definition("ServerEdited"))));

        Assert.Contains(changes, change => change.ChangeType == ChangeType.Update);
    }

    [Fact]
    public async Task RemoteChanges_WhenTheServiceNoLongerHoldsTheWorkflow_ReportsADelete()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Original"));

        var (_, changes) = await GetRemoteChangesAsync((_, _) => Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(null));

        Assert.Equal(ChangeType.Delete, Assert.Single(changes).ChangeType);
    }

    [Fact]
    public async Task RemoteChanges_ReadsEveryWorkflowTheWorkspaceTracks()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Original"));

        var requested = new List<Guid>();

        await GetRemoteChangesAsync((workflowId, _) =>
        {
            requested.Add(workflowId);

            return Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(RemoteRow(Definition("Original")));
        });

        Assert.Equal(_workflowId, Assert.Single(requested));
    }

    [Fact]
    public async Task RemoteChanges_LeavesTheCloudCacheUntouched()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Edited"));

        var cachedBefore = CachedDefinition();

        await GetRemoteChangesAsync((_, _) => Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(RemoteRow(Definition("ServerEdited"))));

        Assert.Equal(cachedBefore, CachedDefinition());
    }

    [Fact]
    public async Task RemoteChanges_ForAStandaloneWorkspace_CarryTheContentThePreviewShows()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Original"));

        var (_, changes) = await GetRemoteChangesAsync((_, _) => Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(RemoteRow(Definition("ServerEdited"))));

        var change = Assert.Single(changes);

        Assert.Equal($"Mcs.Workflow.{_workflowId}", change.SchemaName);
        Assert.Contains("ServerEdited", change.RemoteWorkflowContent!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteChanges_WhenOnlyTheMetadataMoved_CarryTheMetadataPreview()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Original"));

        var renamed = RemoteRow(Definition("Original"));
        renamed.Name = "Renamed Flow";

        var (_, changes) = await GetRemoteChangesAsync((_, _) => Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(renamed));

        var change = Assert.Single(changes);

        Assert.Equal($"Mcs.Workflow.{_workflowId}.metadata", change.SchemaName);
        Assert.Contains("Renamed Flow", change.RemoteWorkflowContent!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteChanges_ForAStandaloneWorkspace_NeedNoAgentId()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Original"));

        var (_, changes) = await GetRemoteChangesAsync((_, _) => Task.FromResult<SyncDataverseClient.WorkflowMetadata?>(RemoteRow(Definition("ServerEdited"))));

        Assert.NotEmpty(changes);
    }

    private async Task<DiscardResult> DiscardAsync()
    {
        var synchronizer = CreateSynchronizer();
        var (_, changes) = await synchronizer.GetLocalChangesAsync(_workspace, new BotDefinition(), CancellationToken.None);

        return synchronizer.DiscardStandaloneWorkflowChanges(_workspace, changes);
    }

    [Fact]
    public async Task Discard_RestoresAnEditedDefinitionFromTheCache()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Edited"));

        var result = await DiscardAsync();

        Assert.Equal(Definition("Original"), File.ReadAllText(Path.Combine(_workflowFolder, "workflow.json")));
        Assert.True(result.Restored > 0);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Discard_LeavesNoLocalChangeBehind()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Edited"));

        await DiscardAsync();

        var (_, remaining) = await GetLocalChangesAsync();

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Discard_DeletesAWorkflowTheCacheNeverHeld()
    {
        WriteLocal(Definition("Original"));

        var result = await DiscardAsync();

        Assert.False(File.Exists(Path.Combine(_workflowFolder, "workflow.json")));
        Assert.False(File.Exists(Path.Combine(_workflowFolder, "metadata.yml")));
        Assert.Equal(2, result.Deleted);
    }

    [Fact]
    public async Task Discard_RestoresAWorkflowThatWasDeletedLocally()
    {
        WriteCache(Definition("Original"));

        var result = await DiscardAsync();

        Assert.True(result.Restored > 0);

        var (_, remaining) = await GetLocalChangesAsync();

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Discard_LeavesTheCloudCacheUntouched()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Edited"));

        var cachedBefore = CachedDefinition();

        await DiscardAsync();

        Assert.Equal(cachedBefore, CachedDefinition());
    }

    [Fact]
    public void Discard_SkipsAChangeThatNamesNoWorkflowFile()
    {
        var change = new Change { SchemaName = "agent1.topic.Greeting", Uri = "topics/Greeting.mcs.yml", ChangeType = ChangeType.Update };

        var result = CreateSynchronizer().DiscardStandaloneWorkflowChanges(_workspace, new[] { change });

        Assert.Equal(0, result.Restored);
        Assert.Equal(0, result.Deleted);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public async Task StandaloneWorkspace_ComputingChanges_LeavesTheCloudCacheUntouched()
    {
        WriteCache(Definition("Original"));
        WriteLocal(Definition("Edited"));

        var cachedDefinitionBefore = CachedDefinition();
        var cachedMetadataBefore = CachedMetadata();

        await GetLocalChangesAsync();

        Assert.Equal(cachedDefinitionBefore, CachedDefinition());
        Assert.Equal(cachedMetadataBefore, CachedMetadata());
    }

    [Fact]
    public async Task StandaloneWorkspace_ComputingChanges_CreatesNoCacheWhenNoneExists()
    {
        WriteLocal(Definition("Original"));

        await GetLocalChangesAsync();

        Assert.False(File.Exists(Path.Combine(_root, ".mcs", "botdefinition.json")));
    }

    [Fact]
    public async Task AgentWorkspaceWithoutACloudCache_StillFailsTheSameWay()
    {
        WriteLocal(Definition("Original"));
        File.WriteAllText(Path.Combine(_root, "settings.mcs.yml"), "kind: GptComponentMetadata\n");

        await Assert.ThrowsAsync<FileNotFoundException>(GetLocalChangesAsync);
    }

    [Fact]
    public async Task WorkspaceHoldingNeitherAgentNorWorkflow_StillFailsTheSameWay()
    {
        File.Delete(Path.Combine(_root, WorkflowWorkspace.ProjectFileName));

        await Assert.ThrowsAsync<FileNotFoundException>(GetLocalChangesAsync);
    }

    [Fact]
    public async Task WorkflowsPresentButUndeclared_IsNotTreatedAsAWorkflowWorkspace()
    {
        File.Delete(Path.Combine(_root, WorkflowWorkspace.ProjectFileName));
        WriteLocal(Definition("Original"));

        await Assert.ThrowsAsync<FileNotFoundException>(GetLocalChangesAsync);
    }
}
