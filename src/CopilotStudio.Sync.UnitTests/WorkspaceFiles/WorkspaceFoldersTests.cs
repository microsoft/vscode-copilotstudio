// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkspaceFoldersTests : IDisposable
{
    private static readonly byte[] NotUtf8 = { 0xFF, 0xFE, 0x00, 0xC3, 0x28, 0xA0, 0xA1 };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcs-folders-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static InMemoryFileAccessor NewWorkspace() => new InMemoryFileAccessor(new DirectoryPath("workspace"));

    private static void WriteBytes(IFileAccessor workspace, string path, byte[] content)
    {
        using var stream = workspace.OpenWrite(new AgentFilePath(path));

        stream.Write(content, 0, content.Length);
    }

    private static SyncDataverseClient.WorkflowMetadata Row(Guid workflowId, string name) => new()
    {
        WorkflowId = workflowId,
        Name = name,
        ClientData = "{}",
        StateCode = 0,
        StatusCode = 1,
    };

    [Fact]
    public void Move_CopiesBytesThatAreNotUtf8Unchanged()
    {
        var workspace = NewWorkspace();

        WriteBytes(workspace, "workflows/source/blob.bin", NotUtf8);

        WorkspaceFolders.Move(workspace, "workflows/source", "workflows/target");

        Assert.Equal(NotUtf8, WorkspaceBytes.Read(workspace, "workflows/target/blob.bin"));
        Assert.False(workspace.Exists(new AgentFilePath("workflows/source/blob.bin")));
    }

    [Fact]
    public void Move_CarriesNestedFolders()
    {
        var workspace = NewWorkspace();

        WriteBytes(workspace, "workflows/source/assets/deep/notes.txt", NotUtf8);

        WorkspaceFolders.Move(workspace, "workflows/source", "workflows/target");

        Assert.Equal(NotUtf8, WorkspaceBytes.Read(workspace, "workflows/target/assets/deep/notes.txt"));
        Assert.Empty(workspace.ListFiles("workflows/source", "*"));
    }

    [Fact]
    public void Move_LeavesASiblingFolderSharingThePrefixAlone()
    {
        var workspace = NewWorkspace();

        WriteBytes(workspace, "workflows/source/workflow.json", NotUtf8);
        WriteBytes(workspace, "workflows/source-copy/workflow.json", NotUtf8);

        WorkspaceFolders.Move(workspace, "workflows/source", "workflows/target");

        Assert.True(workspace.Exists(new AgentFilePath("workflows/source-copy/workflow.json")));
        Assert.False(workspace.Exists(new AgentFilePath("workflows/target-copy/workflow.json")));
    }

    [Fact]
    public void Move_WhenASourceFileCannotBeRead_LeavesEverySourceFileInPlace()
    {
        var workspace = new UnreadableFileAccessor(NewWorkspace(), "workflows/source/metadata.yml");

        WriteBytes(workspace, "workflows/source/workflow.json", NotUtf8);
        WriteBytes(workspace, "workflows/source/metadata.yml", NotUtf8);

        Assert.Throws<IOException>(() => WorkspaceFolders.Move(workspace, "workflows/source", "workflows/target"));

        Assert.True(workspace.Exists(new AgentFilePath("workflows/source/workflow.json")));
        Assert.True(workspace.Exists(new AgentFilePath("workflows/source/metadata.yml")));
    }

    [Fact]
    public async Task Pull_AfterARename_LeavesOneFolderOnDisk()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = WorkflowWorkspace.OpenWorkspace(_root);
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "Before"));
        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);
        client.Seed(Row(workflowId, "After"));

        var pulled = await synchronizer.PullAsync(workflowId, workspace, CancellationToken.None);

        Assert.Equal(WorkflowWorkspace.RelativeFolderFor("Before", workflowId), pulled.Download!.PreviousFolder);
        Assert.Equal([WorkflowWorkspace.FolderNameFor("After", workflowId)], Directory.GetDirectories(WorkflowWorkspace.WorkflowsDirectory(_root)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Pull_WhenTheServiceLostTheWorkflow_RemovesItsFolderFromDisk()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();
        var workspace = WorkflowWorkspace.OpenWorkspace(_root);
        var synchronizer = new StandaloneWorkflowSynchronizer(client);

        client.Seed(Row(workflowId, "Gone"));
        await synchronizer.DownloadAsync(workflowId, workspace, "env", CancellationToken.None);
        await client.DeleteWorkflowAsync(workflowId, CancellationToken.None);

        var pulled = await synchronizer.PullAsync(workflowId, workspace, CancellationToken.None);

        Assert.True(pulled.Deleted);
        Assert.Empty(Directory.GetDirectories(WorkflowWorkspace.WorkflowsDirectory(_root)));
    }

    [Fact]
    public async Task Delete_WhenTheWorkspaceFolderNoLongerExists_StillDeletesWithoutRecreatingIt()
    {
        var workflowId = Guid.NewGuid();
        var client = new FakeStandaloneWorkflowClient();

        client.Seed(Row(workflowId, "Gone"));

        Assert.True(await new StandaloneWorkflowSynchronizer(client).DeleteAsync(workflowId, WorkflowWorkspace.OpenWorkspace(_root), CancellationToken.None));
        Assert.False(Directory.Exists(_root));
    }

    private sealed class UnreadableFileAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;

        private readonly string _unreadable;

        public UnreadableFileAccessor(IFileAccessor inner, string unreadable)
        {
            _inner = inner;
            _unreadable = unreadable;
        }

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public Stream OpenRead(AgentFilePath path) => path.ToString() == _unreadable ? throw new IOException("The file is locked.") : _inner.OpenRead(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public void Delete(AgentFilePath path) => _inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => _inner.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*") => _inner.ListFiles(relativeFolder, filePattern);
    }
}