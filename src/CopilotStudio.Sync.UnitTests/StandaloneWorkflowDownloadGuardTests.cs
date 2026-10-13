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
        Assert.Contains("cloud", Read(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullStillRunsWhenTheWorkspaceMatchesTheService()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId));

        var (synchronizer, workspace, folder) = await ClonedAsync(client, workflowId);
        var moved = client.Stored(workflowId)!;
        moved.ClientData = """{"properties":{"from":"server"}}""";
        client.Seed(moved);

        var pulled = await synchronizer.PullAsync(workflowId, workspace, CancellationToken.None);

        Assert.NotNull(pulled.Download);
        Assert.Contains("server", Read(workspace, $"{folder}/{WorkflowWorkspace.DefinitionFileName}")!, StringComparison.Ordinal);
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
}
