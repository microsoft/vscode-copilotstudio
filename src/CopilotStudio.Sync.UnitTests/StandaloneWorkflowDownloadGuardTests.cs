// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class StandaloneWorkflowDownloadGuardTests
{
    private const string Definition = """{"properties":{"from":"cloud"}}""";

    private static readonly Encoding NoByteOrderMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly StandaloneWorkflowDownloadOptions Overwriting = new() { OverwriteLocalChanges = true };

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

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string definition = Definition) => new()
    {
        WorkflowId = workflowId,
        Name = "My Flow",
        ClientData = definition,
        StateCode = 0,
        StatusCode = 1,
    };

    private static async Task<(StandaloneWorkflowSynchronizer Synchronizer, InMemoryFileAccessor Workspace, string Folder)> ClonedAsync(FakeStandaloneWorkflowClient client, Guid workflowId)
    {
        var workspace = NewWorkspace();
        var synchronizer = new StandaloneWorkflowSynchronizer(client);
        var download = await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        return (synchronizer, workspace, download.Folder);
    }

    private static void Edit(IFileAccessor workspace, string folder) =>
        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", """{"properties":{"from":"local"}}""");

    private static void EditInTheEnvironment(FakeStandaloneWorkflowClient client, Guid workflowId, Action<SyncDataverseClient.WorkflowMetadata> edit)
    {
        var stored = (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        edit(stored);
        client.Seed(stored);
    }

    [Fact]
    public async Task PullRefusesToReplaceAnUnpushedEdit()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => synchronizer.PullAsync(workflowId, workspace, CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.WorkspaceHoldsUnpushedChanges, failure.Failure);
        Assert.Equal("""{"properties":{"from":"local"}}""", Read(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}"));
    }

    [Fact]
    public async Task PullReplacesAnUnpushedEditWhenAskedTo()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);

        var pulled = await synchronizer.PullAsync(workflowId, workspace, CancellationToken.None, Overwriting);

        Assert.NotNull(pulled.Download);
        Assert.Contains("cloud", (Read(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}") ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullStillRunsWhenTheWorkspaceMatchesTheService()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);
        var moved = (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        moved.ClientData = """{"properties":{"from":"server"}}""";
        client.Seed(moved);

        var pulled = await synchronizer.PullAsync(workflowId, workspace, CancellationToken.None);

        Assert.NotNull(pulled.Download);
        Assert.Contains("server", (Read(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}") ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullRefusesToRemoveAnUnpushedEditWhenTheServiceLostTheWorkflow()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        await client.DeleteWorkflowAsync(workflowId, CancellationToken.None);

        await Assert.ThrowsAsync<StandaloneWorkflowException>(() => synchronizer.PullAsync(workflowId, workspace, CancellationToken.None));

        Assert.NotNull(Read(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}"));
    }

    [Fact]
    public async Task DownloadRefusesToReplaceAnUnpushedEdit()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.WorkspaceHoldsUnpushedChanges, failure.Failure);
    }

    [Fact]
    public async Task DownloadIntoAnEmptyWorkspaceIsNeverRefused()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var download = await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, NewWorkspace(), "env", CancellationToken.None);

        Assert.Equal(workflowId, download.Workflow.WorkflowId);
    }

    [Fact]
    public async Task DownloadIsNeverRefusedByAnotherWorkflowsUnpushedEdit()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(mine));
        client.Seed(new SyncDataverseClient.WorkflowMetadata { WorkflowId = other, Name = "Other Flow", ClientData = Definition, StateCode = 0, StatusCode = 1 });

        var synchronizer = new StandaloneWorkflowSynchronizer(client);
        var workspace = NewWorkspace();
        var otherDownload = await synchronizer.DownloadAsync(other, workspace, "env", CancellationToken.None);

        Edit(workspace, otherDownload.Folder);

        var download = await synchronizer.DownloadAsync(mine, workspace, "env", CancellationToken.None);

        Assert.Equal(mine, download.Workflow.WorkflowId);
    }

    [Fact]
    public async Task ACacheLessWorkspaceWhoseMetadataTheServiceWouldReplace_IsRefused()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("My Flow", workflowId);
        var scaffolded = WorkflowScaffolder.NewMetadata("My Flow", workflowId, folder);

        scaffolded.StateCode = 0;
        scaffolded.StatusCode = 1;

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", Definition);
        Write(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(scaffolded));
        client.Seed(Row(workflowId));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(
            () => new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.WorkspaceHoldsUnpushedChanges, failure.Failure);
        Assert.Equal(WorkflowMetadataFile.Write(scaffolded), Read(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}"));
    }

    [Fact]
    public async Task ACacheLessWorkspaceHoldingWhatTheServiceWouldWrite_IsNotRefused()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("My Flow", workflowId);

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", WorkflowDefinitionFile.Format(Definition));
        Write(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(Row(workflowId)));
        client.Seed(Row(workflowId));

        var download = await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.Equal(workflowId, download.Workflow.WorkflowId);
    }

    [Fact]
    public async Task ACacheLessWorkspaceWhoseMetadataTheServiceWouldReplace_YieldsWhenAskedTo()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("My Flow", workflowId);
        var scaffolded = WorkflowScaffolder.NewMetadata("My Flow", workflowId, folder);

        scaffolded.StateCode = 0;
        scaffolded.StatusCode = 1;

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", Definition);
        Write(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(scaffolded));
        client.Seed(Row(workflowId));

        var download = await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None, Overwriting);

        Assert.Equal(workflowId, download.Workflow.WorkflowId);
        Assert.Equal(WorkflowMetadataFile.Write(Row(workflowId)), Read(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}"));
    }

    [Fact]
    public async Task APushedEditIsNoLongerTreatedAsUnpushed()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);

        await synchronizer.UpsertAsync(workspace, "env", CancellationToken.None);

        var pulled = await synchronizer.PullAsync(workflowId, workspace, CancellationToken.None);

        Assert.NotNull(pulled.Download);
    }

    [Fact]
    public async Task PushCheckRefusesToReplaceADefinitionEditedInTheEnvironment()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        EditInTheEnvironment(client, workflowId, row => row.ClientData = """{"properties":{"from":"server"}}""");
        client.Calls.Clear();

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => synchronizer.RequireNoUnpulledChangesAsync(workspace, "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.EnvironmentHoldsUnpulledChanges, failure.Failure);
        Assert.Equal(folder, $"{WorkflowWorkspace.WorkflowsFolderName}/{failure.Subject}");
        Assert.Equal("env", failure.EnvironmentId);
        Assert.Equal($"'{failure.Subject}' has conflict remote change, and applying changes would replace them. Use Get changes to get them, or Apply changes again and choose Overwrite remote changes to replace them.", failure.Message);
        Assert.Equal(["get"], client.Calls);
        Assert.Contains("server", ((client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")), StringComparison.Ordinal);
        Assert.Equal("""{"properties":{"from":"local"}}""", Read(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}"));
    }

    [Fact]
    public async Task PushCheckRefusesToReplaceADescriptionEditedInTheEnvironment()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        EditInTheEnvironment(client, workflowId, row => row.Description = "edited in the designer");

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => synchronizer.RequireNoUnpulledChangesAsync(workspace, "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.EnvironmentHoldsUnpulledChanges, failure.Failure);
    }

    [Fact]
    public async Task PushCheckAllowsALocalEditWhenTheEnvironmentIsUnchanged()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);

        await synchronizer.RequireNoUnpulledChangesAsync(workspace, "env", CancellationToken.None);
    }

    [Fact]
    public async Task PushCheckAllowsAPushThatWouldWriteWhatTheEnvironmentAlreadyHolds()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        EditInTheEnvironment(client, workflowId, row => row.ClientData = """{"properties":{"from":"local"}}""");

        await synchronizer.RequireNoUnpulledChangesAsync(workspace, "env", CancellationToken.None);
    }

    [Fact]
    public async Task PushCheckIgnoresAnActivationChangedInTheEnvironment()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        EditInTheEnvironment(client, workflowId, row =>
        {
            row.StateCode = 1;
            row.StatusCode = 2;
        });

        await synchronizer.RequireNoUnpulledChangesAsync(workspace, "env", CancellationToken.None);
    }

    [Fact]
    public async Task PushCheckAllowsAWorkflowTheEnvironmentNoLongerHolds()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        await client.DeleteWorkflowAsync(workflowId, CancellationToken.None);

        await synchronizer.RequireNoUnpulledChangesAsync(workspace, "env", CancellationToken.None);
    }

    [Fact]
    public async Task PushCheckAllowsAnEditAfterThisWorkspacePushedTheEnvironmentsCopy()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        await synchronizer.UpsertAsync(workspace, "env", CancellationToken.None);
        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", """{"properties":{"from":"second local edit"}}""");

        await synchronizer.RequireNoUnpulledChangesAsync(workspace, "env", CancellationToken.None);
    }

    [Fact]
    public async Task PushCheckOfACacheLessWorkspaceThatWouldReplaceTheEnvironmentsCopy_IsRefused()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("My Flow", workflowId);

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", """{"properties":{"from":"local"}}""");
        Write(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(Row(workflowId)));
        client.Seed(Row(workflowId));

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => new StandaloneWorkflowSynchronizer(client).RequireNoUnpulledChangesAsync(workspace, "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.EnvironmentHoldsUnpulledChanges, failure.Failure);
    }

    [Fact]
    public async Task UpsertStillReplacesAnEditMadeInTheEnvironmentWhenNotAskedToCheck()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        EditInTheEnvironment(client, workflowId, row => row.ClientData = """{"properties":{"from":"server"}}""");

        await synchronizer.UpsertAsync(workspace, "env", CancellationToken.None);

        Assert.Contains("local", ((client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")), StringComparison.Ordinal);
    }

    private const string ReferencingDefinition = """{"properties":{"connectionReferences":{"shared_teams":{"api":{"name":"shared_teams"},"connection":{"connectionReferenceLogicalName":"new_teams"}}},"from":"local"}}""";

    private const string DraftRefusal = "Failed to update workflow: Dataverse request failed (400): {\"error\":{\"code\":\"0x80040203\",\"message\":\"You are attempting to do a published update of publishable component in an unmodified active context when there exists an unpublished active row.\\r\\nThis is not allowed context.IsModified =False Component Type: 29 CurrentState=ActiveUnpublished\"}}";

    [Fact]
    public async Task UpsertRefusesToWriteOverAnUnpublishedDraftBeforeWritingAnything()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", ReferencingDefinition);
        client.Drafts.Add(workflowId);
        client.Calls.Clear();
        client.DraftChecks.Clear();

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => synchronizer.UpsertAsync(workspace, "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.EnvironmentHoldsUnpublishedDraft, failure.Failure);
        Assert.Equal("My Flow", failure.Subject);
        Assert.Equal("env", failure.EnvironmentId);
        Assert.Equal("'My Flow' has a draft version that was saved in Copilot Studio but not published, and the environment refuses to apply changes while it exists. Publish the draft in Copilot Studio, then use Get changes to get it, or Apply changes again and choose Overwrite remote changes to replace it.", failure.Message);
        Assert.Equal(["get"], client.Calls);
        Assert.Equal([workflowId], client.DraftChecks);
        Assert.Empty(client.ConnectionReferences);
        Assert.Equal(Definition, (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData);
        Assert.Equal(Definition, (StandaloneWorkflowCache.Read(workspace, workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData);
    }

    [Fact]
    public async Task UpsertOfAWorkflowTheEnvironmentHoldsOnlyAsADraft_IsRefusedWithoutCreatingIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = NewWorkspace();
        var folder = WorkflowWorkspace.RelativeFolderFor("My Flow", workflowId);

        Write(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}", Definition);
        Write(workspace, $"{folder}/{WorkflowWorkspace.MetadataFileName}", WorkflowMetadataFile.Write(Row(workflowId)));
        client.Drafts.Add(workflowId);

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => new StandaloneWorkflowSynchronizer(client).UpsertAsync(workspace, "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.EnvironmentHoldsUnpublishedDraft, failure.Failure);
        Assert.DoesNotContain("insert", client.Calls);
        Assert.Null(client.Stored(workflowId));
    }

    [Fact]
    public async Task UpsertWithNothingToWrite_ReadsNoDraftAndSucceeds()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, _) = await ClonedAsync(client, workflowId);

        client.Drafts.Add(workflowId);
        client.DraftChecks.Clear();

        var upsert = await synchronizer.UpsertAsync(workspace, "env", CancellationToken.None);

        Assert.False(upsert.Created);
        Assert.Empty(client.DraftChecks);
        Assert.DoesNotContain("update", client.Calls);
    }

    [Fact]
    public async Task UpsertWithoutADraft_ChecksOnceAndWrites()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);
        client.DraftChecks.Clear();

        await synchronizer.UpsertAsync(workspace, "env", CancellationToken.None);

        Assert.Equal([workflowId], client.DraftChecks);
        Assert.Contains("local", ((client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteRefusedOverADraftSavedDuringThePush_IsReportedAsTheDraft()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient { UpdateFailure = DraftRefusal };

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);

        Edit(workspace, folder);

        var failure = await Assert.ThrowsAsync<StandaloneWorkflowException>(() => synchronizer.UpsertAsync(workspace, "env", CancellationToken.None));

        Assert.Equal(StandaloneWorkflowFailure.EnvironmentHoldsUnpublishedDraft, failure.Failure);
        Assert.Equal("My Flow", failure.Subject);
        Assert.Equal(Definition, (client.Stored(workflowId) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).ClientData);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ADownloadWritesThePublishedVersionAndWarnsOnlyWhenADraftExists(bool draft)
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        if (draft)
        {
            client.Drafts.Add(workflowId);
        }

        var workspace = NewWorkspace();
        var download = await new StandaloneWorkflowSynchronizer(client).DownloadAsync(workflowId, workspace, "env", CancellationToken.None);

        Assert.Equal(draft ? "'My Flow' also has a draft in env that was saved in the designer but not published, so only the published version was written. Publish the draft in the designer to get it." : string.Empty, download.Warning);
        Assert.Contains("cloud", Read(workspace, $"{download.Folder}/{WorkflowWorkspace.DefinitionFileName}"), StringComparison.Ordinal);
        Assert.Equal([workflowId], client.DraftChecks);
    }
}
