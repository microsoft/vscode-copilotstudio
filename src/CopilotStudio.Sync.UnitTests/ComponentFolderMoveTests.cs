// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Xunit;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ComponentFolderMoveTests
{
    [Fact]
    public void Move_RelocatesEveryFileAndClearsTheSource()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/move-basic/"));
        Write(accessor, "workflows/Old/definition.json", "definition");
        Write(accessor, "workflows/Old/nested/extra.json", "extra");

        WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New");

        Assert.Equal("definition", Read(accessor, "workflows/New/definition.json"));
        Assert.Equal("extra", Read(accessor, "workflows/New/nested/extra.json"));
        Assert.False(accessor.Exists(new AgentFilePath("workflows/Old/definition.json")));
    }

    [Fact]
    public void Move_OverExistingTarget_ReplacesTargetContent()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/move-over-target/"));
        Write(accessor, "workflows/Old/definition.json", "source");
        Write(accessor, "workflows/New/definition.json", "stale-target");
        Write(accessor, "workflows/New/orphan.json", "stale-orphan");

        WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New");

        Assert.Equal("source", Read(accessor, "workflows/New/definition.json"));
        Assert.False(accessor.Exists(new AgentFilePath("workflows/New/orphan.json")));
    }

    [Fact]
    public void Move_LeavesNoPreservationFolderBehind()
    {
        using var factory = new ProductionFileAccessorFactory();
        var accessor = factory.Create(new DirectoryPath("c:/test/move-no-litter/"));
        Write(accessor, "workflows/Old/definition.json", "source");
        Write(accessor, "workflows/New/definition.json", "stale-target");

        WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New");

        Assert.DoesNotContain(accessor.ListFiles().Select(file => file.ToString()), path => path.Contains(".move.", StringComparison.Ordinal));
    }

    [Fact]
    public void Move_WhenReplaceFails_RestoresTheOriginalTarget()
    {
        using var inner = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/move-restore-target/");
        var accessor = new FailOnMoveIntoTargetAccessor(inner.Create(root));
        Write(accessor, "workflows/Old/definition.json", "source");
        Write(accessor, "workflows/New/definition.json", "must-survive");

        Assert.ThrowsAny<Exception>(() => WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New"));

        Assert.Equal("must-survive", Read(accessor, "workflows/New/definition.json"));
    }

    [Fact]
    public void Move_WhenReplaceFails_RestoresTheSourceFiles()
    {
        using var inner = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/move-restore-source/");
        var accessor = new FailOnMoveIntoTargetAccessor(inner.Create(root));
        Write(accessor, "workflows/Old/definition.json", "source");
        Write(accessor, "workflows/New/definition.json", "must-survive");

        Assert.ThrowsAny<Exception>(() => WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New"));

        Assert.Equal("source", Read(accessor, "workflows/Old/definition.json"));
    }

    [Fact]
    public void Move_OverPopulatedTarget_LeavesNoPreservationFolderOnDisk()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "mcs-move-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(rootPath);
        try
        {
            var accessor = new FileAccessorFactory().Create(new DirectoryPath(rootPath.Replace('\\', '/') + "/"));
            Write(accessor, "workflows/Old/definition.json", "source");
            Write(accessor, "workflows/New/definition.json", "stale-target");

            WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New");

            Assert.Empty(Directory.GetDirectories(Path.Combine(rootPath, "workflows"), "*.move.*", SearchOption.AllDirectories));
            Assert.Equal("source", Read(accessor, "workflows/New/definition.json"));
        }
        finally
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_MemoryBackedWithNonStreamingClient_FailsInsteadOfStagingOnDisk()
    {
        using var factory = new ProductionFileAccessorFactory();
        var synchronizer = new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            factory,
            new Moq.Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

        var workspace = new DirectoryPath("c:/test/memory-non-streaming/");
        var fileAccessor = factory.Create(workspace);
        KnowledgeFileTestFixtures.WriteBotCloudCache(fileAccessor, KnowledgeFileTestFixtures.CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid()));

        var nonStreamingClient = new Moq.Mock<Dataverse.ISyncDataverseClient>(Moq.MockBehavior.Loose);

        await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.DownloadKnowledgeFilesAsync(workspace, nonStreamingClient.Object, schemaNames: null, CancellationToken.None));
    }

    [Fact]
    public void Move_WhenPreservingTargetFails_RestoresEveryPreservedTargetFile()
    {
        using var inner = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/move-preserve-fails/");
        var accessor = new FailOnSecondPreservationAccessor(inner.Create(root));
        Write(accessor, "workflows/Old/definition.json", "source");
        Write(accessor, "workflows/New/first.json", "first-target");
        Write(accessor, "workflows/New/second.json", "second-target");

        Assert.ThrowsAny<Exception>(() => WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New"));

        Assert.Equal("first-target", Read(accessor, "workflows/New/first.json"));
        Assert.Equal("second-target", Read(accessor, "workflows/New/second.json"));
    }

    [Fact]
    public void Move_WhenPreservingTargetFails_LeavesNothingInThePreservationFolder()
    {
        using var inner = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/move-preserve-fails-litter/");
        var accessor = new FailOnSecondPreservationAccessor(inner.Create(root));
        Write(accessor, "workflows/Old/definition.json", "source");
        Write(accessor, "workflows/New/first.json", "first-target");
        Write(accessor, "workflows/New/second.json", "second-target");

        Assert.ThrowsAny<Exception>(() => WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New"));

        Assert.DoesNotContain(accessor.ListFiles().Select(file => file.ToString()), path => path.Contains(".move.", StringComparison.Ordinal));
    }

    [Fact]
    public void Move_WhenPreservingTargetFails_KeepsTheSourceIntact()
    {
        using var inner = new ProductionFileAccessorFactory();
        var root = new DirectoryPath("c:/test/move-preserve-fails-source/");
        var accessor = new FailOnSecondPreservationAccessor(inner.Create(root));
        Write(accessor, "workflows/Old/definition.json", "source");
        Write(accessor, "workflows/New/first.json", "first-target");
        Write(accessor, "workflows/New/second.json", "second-target");

        Assert.ThrowsAny<Exception>(() => WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New"));

        Assert.Equal("source", Read(accessor, "workflows/Old/definition.json"));
    }

    [Fact]
    public void Move_WhenPreservingTargetFails_LeavesNoPreservationDirectoryOnDisk()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "mcs-move-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(rootPath);
        try
        {
            var accessor = new FailOnSecondPreservationAccessor(new FileAccessorFactory().Create(new DirectoryPath(rootPath.Replace('\\', '/') + "/")));
            Write(accessor, "workflows/Old/definition.json", "source");
            Write(accessor, "workflows/New/first.json", "first-target");
            Write(accessor, "workflows/New/second.json", "second-target");

            Assert.ThrowsAny<Exception>(() => WorkspaceSynchronizer.MoveComponentFolder(accessor, "workflows/Old", "workflows/New"));

            Assert.Empty(Directory.GetDirectories(Path.Combine(rootPath, "workflows"), "*.move.*", SearchOption.AllDirectories));
            Assert.Equal("first-target", Read(accessor, "workflows/New/first.json"));
            Assert.Equal("second-target", Read(accessor, "workflows/New/second.json"));
        }
        finally
        {
            try
            {
                Directory.Delete(rootPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Write(IFileAccessor accessor, string path, string content)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Read(IFileAccessor accessor, string path)
    {
        using var stream = accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class FailOnMoveIntoTargetAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;

        public FailOnMoveIntoTargetAccessor(IFileAccessor inner) => _inner = inner;

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public void Delete(AgentFilePath path) => _inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*") => _inner.ListFiles(relativeFolder, filePattern);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
        {
            if (sourcePath.ToString().StartsWith("workflows/Old/", StringComparison.Ordinal))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            _inner.Replace(sourcePath, targetPath);
        }
    }

    private sealed class FailOnSecondPreservationAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;
        private int _preservationCount;

        public FailOnSecondPreservationAccessor(IFileAccessor inner) => _inner = inner;

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public void Delete(AgentFilePath path) => _inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*") => _inner.ListFiles(relativeFolder, filePattern);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
        {
            if (targetPath.ToString().Contains(".move.", StringComparison.Ordinal)
                && ++_preservationCount == 2)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            _inner.Replace(sourcePath, targetPath);
        }
    }
}

public class NonSeekableAccessorUploadTests
{
    [Fact]
    public void GetFileSize_OverNonSeekableStream_CountsBytes()
    {
        using var inner = new ProductionFileAccessorFactory();
        var accessor = new NonSeekableReadAccessor(inner.Create(new DirectoryPath("c:/test/non-seekable/")));
        var path = new AgentFilePath("knowledge/files/Doc.txt");
        using (var stream = accessor.OpenWrite(path))
        {
            stream.Write(new byte[4096], 0, 4096);
        }

        using var read = accessor.OpenRead(path);

        Assert.False(read.CanSeek);
        Assert.Throws<NotSupportedException>(() => read.Length);
    }

    [Fact]
    public async Task UploadKnowledgeFiles_OverNonSeekableAccessor_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/non-seekable-upload/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        using (var stream = fileAccessor.OpenWrite(new AgentFilePath("knowledge/files/Doc.txt")))
        {
            stream.Write(new byte[64], 0, 64);
        }

        var uploaded = await synchronizer.UploadKnowledgeFilesAsync(workspace, new Moq.Mock<Dataverse.ISyncDataverseClient>().Object, CancellationToken.None);

        Assert.Empty(uploaded);
    }

    private sealed class NonSeekableReadAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;

        public NonSeekableReadAccessor(IFileAccessor inner) => _inner = inner;

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => new ForwardOnlyStream(_inner.OpenRead(path));

        public void Delete(AgentFilePath path) => _inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => _inner.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*") => _inner.ListFiles(relativeFolder, filePattern);
    }

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly Stream _inner;

        public ForwardOnlyStream(Stream inner) => _inner = inner;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
