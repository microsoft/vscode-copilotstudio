// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CopilotStudio.McsCore;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class RemoteBindingSnapshotTests
{
    private const string ConnectionDetailsFile = ".mcs/conn.json";

    private static readonly string[] BindingFiles =
    {
        ".mcs/botdefinition.json",
        ".mcs/botdefinition.yml",
        ".mcs/changetoken.txt",
        ".mcs/.connections-cache.json",
        ".mcs/.connectors-sync.json",
        ".mcs/.knowledge-sync.json",
        ".mcs/.aiprompts-sync.json",
    };

    [Fact]
    public async Task ResetThenRestore_RestoresAllBindingAndBaselineFiles()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        await fileAccessor.WriteAsync(new AgentFilePath(ConnectionDetailsFile), "old-env-binding", CancellationToken.None);
        foreach (var file in BindingFiles)
        {
            await fileAccessor.WriteAsync(new AgentFilePath(file), $"original::{file}", CancellationToken.None);
        }

        var snapshot = synchronizer.ResetRemoteBindingState(workspace);

        foreach (var file in BindingFiles)
        {
            Assert.False(fileAccessor.Exists(new AgentFilePath(file)));
        }
        Assert.True(fileAccessor.Exists(new AgentFilePath(ConnectionDetailsFile)));

        await fileAccessor.WriteAsync(new AgentFilePath(ConnectionDetailsFile), "new-env-binding-partial", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/.connectors-sync.json"), "dirty-during-retarget", CancellationToken.None);

        synchronizer.RestoreRemoteBindingState(workspace, snapshot);

        Assert.Equal("old-env-binding", await fileAccessor.ReadStringAsync(new AgentFilePath(ConnectionDetailsFile), CancellationToken.None));
        foreach (var file in BindingFiles)
        {
            Assert.True(fileAccessor.Exists(new AgentFilePath(file)));
            var content = await fileAccessor.ReadStringAsync(new AgentFilePath(file), CancellationToken.None);
            Assert.Equal($"original::{file}", content);
        }
    }

    [Fact]
    public async Task ResetThenRestore_LeavesPreviouslyAbsentFilesAbsent()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        await fileAccessor.WriteAsync(new AgentFilePath(ConnectionDetailsFile), "old-env-binding", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/botdefinition.json"), "bot", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token", CancellationToken.None);

        var snapshot = synchronizer.ResetRemoteBindingState(workspace);

        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/.connectors-sync.json"), "created-during-retarget", CancellationToken.None);

        synchronizer.RestoreRemoteBindingState(workspace, snapshot);

        Assert.Equal("old-env-binding", await fileAccessor.ReadStringAsync(new AgentFilePath(ConnectionDetailsFile), CancellationToken.None));
        Assert.Equal("bot", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/botdefinition.json"), CancellationToken.None));
        Assert.Equal("token", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));
        Assert.False(fileAccessor.Exists(new AgentFilePath(".mcs/.connectors-sync.json")));
    }

    [Fact]
    public async Task Reset_WhenDeleteFailsMidway_RestoresAlreadyDeletedFiles()
    {
        var fileParser = new SyncMcsFileParser(LspProjectorService.Instance);
        var fileAccessorFactory = new ThrowingDeleteFileAccessorFactory(".mcs/changetoken.txt");
        var synchronizer = new WorkspaceSynchronizer(
            fileParser,
            fileAccessorFactory,
            new Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        await fileAccessor.WriteAsync(new AgentFilePath(ConnectionDetailsFile), "old-env-binding", CancellationToken.None);
        foreach (var file in BindingFiles)
        {
            await fileAccessor.WriteAsync(new AgentFilePath(file), $"original::{file}", CancellationToken.None);
        }

        await Assert.ThrowsAsync<IOException>(() => Task.FromResult(synchronizer.ResetRemoteBindingState(workspace)));

        Assert.Equal("old-env-binding", await fileAccessor.ReadStringAsync(new AgentFilePath(ConnectionDetailsFile), CancellationToken.None));
        foreach (var file in BindingFiles)
        {
            Assert.True(fileAccessor.Exists(new AgentFilePath(file)), $"{file} should be restored after a failed reset");
            var content = await fileAccessor.ReadStringAsync(new AgentFilePath(file), CancellationToken.None);
            Assert.Equal($"original::{file}", content);
        }
    }

    [Fact]
    public async Task PersistThenFinalize_PushFailed_RestoresPreviousBinding()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        await fileAccessor.WriteAsync(new AgentFilePath(ConnectionDetailsFile), "old-env-binding", CancellationToken.None);
        foreach (var file in BindingFiles)
        {
            await fileAccessor.WriteAsync(new AgentFilePath(file), $"original::{file}", CancellationToken.None);
        }

        var snapshot = synchronizer.ResetRemoteBindingState(workspace);
        synchronizer.PersistRetargetBackup(workspace, snapshot);

        await fileAccessor.WriteAsync(new AgentFilePath(ConnectionDetailsFile), "new-env-binding", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/botdefinition.json"), "new-cache", CancellationToken.None);

        var finalized = synchronizer.FinalizeRetarget(workspace, pushSucceeded: false);

        Assert.True(finalized);
        Assert.Equal("old-env-binding", await fileAccessor.ReadStringAsync(new AgentFilePath(ConnectionDetailsFile), CancellationToken.None));
        foreach (var file in BindingFiles)
        {
            Assert.Equal($"original::{file}", await fileAccessor.ReadStringAsync(new AgentFilePath(file), CancellationToken.None));
        }
        Assert.False(fileAccessor.Exists(new AgentFilePath(".mcs/.retarget-backup.json")));
    }

    [Fact]
    public async Task PersistThenFinalize_PushSucceeded_DiscardsBackupAndKeepsNewBinding()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        await fileAccessor.WriteAsync(new AgentFilePath(ConnectionDetailsFile), "old-env-binding", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "old-token", CancellationToken.None);

        var snapshot = synchronizer.ResetRemoteBindingState(workspace);
        synchronizer.PersistRetargetBackup(workspace, snapshot);

        await fileAccessor.WriteAsync(new AgentFilePath(ConnectionDetailsFile), "new-env-binding", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "new-token", CancellationToken.None);

        var finalized = synchronizer.FinalizeRetarget(workspace, pushSucceeded: true);

        Assert.True(finalized);
        Assert.Equal("new-env-binding", await fileAccessor.ReadStringAsync(new AgentFilePath(ConnectionDetailsFile), CancellationToken.None));
        Assert.Equal("new-token", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));
        Assert.False(fileAccessor.Exists(new AgentFilePath(".mcs/.retarget-backup.json")));
    }

    [Fact]
    public void FinalizeRetarget_WhenNoBackup_ReturnsFalse()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");

        Assert.False(synchronizer.FinalizeRetarget(workspace, pushSucceeded: false));
        Assert.False(synchronizer.FinalizeRetarget(workspace, pushSucceeded: true));
    }

    private sealed class ThrowingDeleteFileAccessorFactory : IFileAccessorFactory
    {
        private readonly string _throwOnDeletePath;
        private readonly Dictionary<string, ThrowingDeleteFileAccessor> _accessors = new();

        public ThrowingDeleteFileAccessorFactory(string throwOnDeletePath)
        {
            _throwOnDeletePath = throwOnDeletePath;
        }

        public IFileAccessor Create(DirectoryPath workspaceFolder)
        {
            var key = workspaceFolder.ToString();
            if (!_accessors.TryGetValue(key, out var accessor))
            {
                accessor = new ThrowingDeleteFileAccessor(workspaceFolder, _throwOnDeletePath);
                _accessors[key] = accessor;
            }
            return accessor;
        }
    }

    private sealed class ThrowingDeleteFileAccessor : IFileAccessor
    {
        private readonly InMemoryFileAccessor _inner;
        private readonly string _throwOnDeletePath;

        public ThrowingDeleteFileAccessor(DirectoryPath root, string throwOnDeletePath)
        {
            _inner = new InMemoryFileAccessor(root);
            _throwOnDeletePath = throwOnDeletePath;
        }

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => _inner.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*") => _inner.ListFiles(relativeFolder, filePattern);

        public void Delete(AgentFilePath path)
        {
            if (path.ToString() == _throwOnDeletePath)
            {
                throw new IOException($"Simulated delete failure for {path}");
            }
            _inner.Delete(path);
        }
    }
}
