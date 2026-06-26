// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Moq;
using System.Collections.Immutable;
using System.Text;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class RestoreRemoteBindingAtomicityTests
{
    private const string BotCache = ".mcs/botdefinition.json";
    private const string ChangeToken = ".mcs/changetoken.txt";
    private const string ConnDetails = ".mcs/conn.json";

    [Fact]
    public void RestoreRemoteBindingState_WhenWriteFailsMidway_RollsBackToCurrentState()
    {
        var factory = new FaultingFileAccessorFactory(failWritePath: ChangeToken);
        var synchronizer = new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            factory,
            new Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

        var workspace = new DirectoryPath("c:/test/restore-atomicity/");
        var inner = factory.Inner(workspace);

        WriteText(inner, BotCache, "target-bot");
        WriteText(inner, ChangeToken, "target-token");
        WriteText(inner, ConnDetails, "target-conn");

        var snapshot = new RemoteBindingSnapshot(ImmutableArray.Create(
            new RemoteBindingFile(new AgentFilePath(BotCache), Encoding.UTF8.GetBytes("source-bot")),
            new RemoteBindingFile(new AgentFilePath(ChangeToken), Encoding.UTF8.GetBytes("source-token")),
            new RemoteBindingFile(new AgentFilePath(ConnDetails), Encoding.UTF8.GetBytes("source-conn"))));

        Assert.Throws<IOException>(() => synchronizer.RestoreRemoteBindingState(workspace, snapshot));

        Assert.Equal("target-bot", ReadText(inner, BotCache));
        Assert.Equal("target-token", ReadText(inner, ChangeToken));
        Assert.Equal("target-conn", ReadText(inner, ConnDetails));
    }

    [Fact]
    public void RestoreRemoteBindingState_WhenDeleteFailsMidway_RollsBackToCurrentState()
    {
        var factory = new FaultingFileAccessorFactory(failDeletePath: ChangeToken);
        var synchronizer = new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            factory,
            new Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

        var workspace = new DirectoryPath("c:/test/restore-atomicity-delete/");
        var inner = factory.Inner(workspace);

        WriteText(inner, BotCache, "target-bot");
        WriteText(inner, ChangeToken, "target-token");
        WriteText(inner, ConnDetails, "target-conn");

        var snapshot = new RemoteBindingSnapshot(ImmutableArray.Create(
            new RemoteBindingFile(new AgentFilePath(BotCache), Encoding.UTF8.GetBytes("source-bot")),
            new RemoteBindingFile(new AgentFilePath(ChangeToken), Encoding.UTF8.GetBytes("source-token")),
            new RemoteBindingFile(new AgentFilePath(ConnDetails), Encoding.UTF8.GetBytes("source-conn"))));

        Assert.Throws<IOException>(() => synchronizer.RestoreRemoteBindingState(workspace, snapshot));

        Assert.Equal("target-bot", ReadText(inner, BotCache));
        Assert.Equal("target-token", ReadText(inner, ChangeToken));
        Assert.Equal("target-conn", ReadText(inner, ConnDetails));
    }

    private static void WriteText(IFileAccessor accessor, string path, string text)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string ReadText(IFileAccessor accessor, string path)
    {
        using var stream = accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class FaultingFileAccessorFactory : IFileAccessorFactory
    {
        private readonly InMemoryFileAccessorFactory _inner = new();
        private readonly string? _failWritePath;
        private readonly string? _failDeletePath;
        private bool _writeFailed;
        private bool _deleteFailed;

        public FaultingFileAccessorFactory(string? failWritePath = null, string? failDeletePath = null)
        {
            _failWritePath = failWritePath;
            _failDeletePath = failDeletePath;
        }

        public IFileAccessor Create(DirectoryPath root) => new FaultingFileAccessor(_inner.Create(root), this);

        public InMemoryFileAccessor Inner(DirectoryPath root) => (InMemoryFileAccessor)_inner.Create(root);

        public bool TryConsumeWriteFailure(string path)
        {
            if (!_writeFailed && path == _failWritePath)
            {
                _writeFailed = true;
                return true;
            }
            return false;
        }

        public bool TryConsumeDeleteFailure(string path)
        {
            if (!_deleteFailed && path == _failDeletePath)
            {
                _deleteFailed = true;
                return true;
            }
            return false;
        }
    }

    private sealed class FaultingFileAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;
        private readonly FaultingFileAccessorFactory _owner;

        public FaultingFileAccessor(IFileAccessor inner, FaultingFileAccessorFactory owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public Stream OpenWrite(AgentFilePath path)
        {
            if (_owner.TryConsumeWriteFailure(path.ToString()))
            {
                throw new IOException("Simulated write failure");
            }
            return _inner.OpenWrite(path);
        }

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public void Delete(AgentFilePath path)
        {
            if (_owner.TryConsumeDeleteFailure(path.ToString()))
            {
                throw new IOException("Simulated delete failure");
            }
            _inner.Delete(path);
        }

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => _inner.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*") => _inner.ListFiles(relativeFolder, filePattern);
    }
}
