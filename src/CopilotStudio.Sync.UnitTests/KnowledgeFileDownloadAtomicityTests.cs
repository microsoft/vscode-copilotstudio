// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.KnowledgeFileTestFixtures;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class KnowledgeFileDownloadAtomicityTests : IDisposable
{
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private const string SharingViolationMessage =
        "The process cannot access the file because it is being used by another process.";

    private const string Lines = "Line one\nLine two\n";

    private static readonly string ManyLines = string.Concat(Enumerable.Repeat(Lines, 12000));

    private static readonly byte[] ManyBytes = Enumerable.Range(0, 200000).Select(index => (byte)(index % 251)).ToArray();

    private static readonly DateTime CheckedOutOn = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "knowledge-download-" + Guid.NewGuid().ToString("N"));

    public static TheoryData<byte[], byte[]> DownloadsTheCheckoutHolds => new()
    {
        { ConnectionReferenceStoreTests.CheckedOut(Lines, "\r\n", byteOrderMark: false), Encoding.UTF8.GetBytes(Lines) },
        { Encoding.UTF8.GetBytes(Lines), ConnectionReferenceStoreTests.CheckedOut(Lines, "\r\n", byteOrderMark: false) },
        { ConnectionReferenceStoreTests.CheckedOut(Lines, "\n", byteOrderMark: true), Encoding.UTF8.GetBytes(Lines) },
        { Encoding.UTF8.GetBytes(Lines), ConnectionReferenceStoreTests.CheckedOut(Lines, "\n", byteOrderMark: true) },
        { ConnectionReferenceStoreTests.CheckedOut(Lines, "\r\n", byteOrderMark: true), Encoding.UTF8.GetBytes(Lines) },
        { Encoding.UTF8.GetBytes(Lines), Encoding.UTF8.GetBytes(Lines) },
        { ClassicAgentFixtures.Icon, ClassicAgentFixtures.Icon },
        { ConnectionReferenceStoreTests.CheckedOut(ManyLines, "\r\n", byteOrderMark: false), Encoding.UTF8.GetBytes(ManyLines) },
        { ManyBytes, ManyBytes.ToArray() },
    };

    public static TheoryData<byte[], byte[]> DownloadsTheCheckoutDoesNotHold => new()
    {
        { ConnectionReferenceStoreTests.CheckedOut(Lines, "\r\n", byteOrderMark: false), Encoding.UTF8.GetBytes("Line one\nLine 2\n") },
        { ClassicAgentFixtures.Icon, ClassicAgentFixtures.Icon.Where((_, index) => index != 4).ToArray() },
        { Encoding.UTF8.GetBytes("Line\0one\r\n"), Encoding.UTF8.GetBytes("Line\0one\n") },
        { ManyBytes, ManyBytes.Take(ManyBytes.Length - 1).Append((byte)0).ToArray() },
        { new byte[] { 0xFF, 0x01, 0x00 }, new byte[] { 0xFF, 0x01 } },
        { Encoding.UTF8.GetBytes("Line one\rLine two\r"), Encoding.UTF8.GetBytes(Lines) },
        { Encoding.UTF8.GetBytes("Line one\nLine two"), Encoding.UTF8.GetBytes(Lines) },
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(DownloadsTheCheckoutHolds))]
    public async Task DownloadKnowledgeFile_OverAFileHoldingTheDownloadApartFromLineEndsOrAByteOrderMark_KeepsItsBytesAndTimestampAsTheBaselineAndThePushUploadsNothing(byte[] checkedOut, byte[] download)
    {
        var (synchronizer, workspace, contentFile) = CheckOutKnowledgeFile(checkedOut);

        Assert.Single(await synchronizer.DownloadKnowledgeFilesAsync(workspace, CreateStreamingMock(destination => destination.WriteAsync(download, 0, download.Length)).Object, schemaNames: null, CancellationToken.None));

        var push = CreateStreamingMock(_ => Task.CompletedTask);
        Assert.Equal(checkedOut, File.ReadAllBytes(contentFile));
        Assert.Equal(CheckedOutOn, File.GetLastWriteTimeUtc(contentFile));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.download.tmp", SearchOption.AllDirectories));
        Assert.Equal(KnowledgeHashOf(checkedOut), Assert.Single(ReadKnowledgeSyncState(new FileAccessorFactory().Create(workspace))).Value);
        Assert.Empty(await synchronizer.UploadKnowledgeFilesAsync(workspace, push.Object, CancellationToken.None));
        push.As<IStreamingKnowledgeFileClient>().Verify(client => client.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [MemberData(nameof(DownloadsTheCheckoutDoesNotHold))]
    public async Task DownloadKnowledgeFile_OverAFileHoldingOtherContent_WritesTheDownloadAndRecordsItsHash(byte[] checkedOut, byte[] download)
    {
        var (synchronizer, workspace, contentFile) = CheckOutKnowledgeFile(checkedOut);

        Assert.Single(await synchronizer.DownloadKnowledgeFilesAsync(workspace, CreateStreamingMock(destination => destination.WriteAsync(download, 0, download.Length)).Object, schemaNames: null, CancellationToken.None));

        Assert.Equal(download, File.ReadAllBytes(contentFile));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.download.tmp", SearchOption.AllDirectories));
        Assert.Equal(KnowledgeHashOf(download), Assert.Single(ReadKnowledgeSyncState(new FileAccessorFactory().Create(workspace))).Value);
    }

    [Fact]
    public async Task DownloadKnowledgeFile_TransferFailsMidStream_LeavesExistingContentIntact()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-partial-failure/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid());
        WriteBotCloudCache(fileAccessor, fileComponent);

        const string Existing = "the-user-still-has-this-content";
        var seeded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace,
            CreateStreamingMock(destination => SeedContent(destination, Existing)).Object,
            schemaNames: null,
            CancellationToken.None);

        var contentPath = new AgentFilePath(Assert.Single(seeded).RelativePath);
        Assert.Equal(Existing, await fileAccessor.ReadStringAsync(contentPath, CancellationToken.None));

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace, CreateStreamingMock(WriteThenThrow).Object, schemaNames: null, CancellationToken.None);

        Assert.Empty(downloaded);
        Assert.Equal(Existing, await fileAccessor.ReadStringAsync(contentPath, CancellationToken.None));
        Assert.DoesNotContain(
            fileAccessor.ListFiles().Select(file => file.ToString()),
            file => file.EndsWith(".download.tmp", StringComparison.OrdinalIgnoreCase));

        static Task WriteThenThrow(Stream destination)
        {
            var partial = Encoding.UTF8.GetBytes("half-a-payl");
            destination.Write(partial, 0, partial.Length);
            destination.Flush();
            throw new DataverseRequestException(
                System.Net.HttpStatusCode.NotFound,
                "{\"error\":{\"message\":\"No file attachment found for attribute: filedata\"}}");
        }
    }

    [Fact]
    public async Task DownloadKnowledgeFile_DestinationLockedOnce_RetriesAndSucceeds()
    {
        var faultingFactory = new FailOnFirstReplaceFactory();
        var synchronizer = new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            faultingFactory,
            new Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

        var workspace = new DirectoryPath("c:/test/promote-retry/");
        var fileAccessor = faultingFactory.Create(workspace);

        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid());
        WriteBotCloudCache(fileAccessor, fileComponent);

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace,
            CreateStreamingMock(destination => SeedContent(destination, "payload-after-retry")).Object,
            schemaNames: null,
            CancellationToken.None);

        var info = Assert.Single(downloaded);
        Assert.Equal(1, faultingFactory.TotalFailures);
        Assert.Equal("payload-after-retry", await fileAccessor.ReadStringAsync(new AgentFilePath(info.RelativePath), CancellationToken.None));
    }

    [Fact]
    public async Task DownloadKnowledgeFile_PromotionExhaustsRetries_LeavesNoOrphanedStagingFile()
    {
        var faultingFactory = new AlwaysFailReplaceFactory();
        var synchronizer = new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            faultingFactory,
            new Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress(new List<string>()),
            new LspComponentPathResolver());

        var workspace = new DirectoryPath("c:/test/promote-exhausted/");
        var fileAccessor = faultingFactory.Create(workspace);

        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid());
        WriteBotCloudCache(fileAccessor, fileComponent);

        var contentPath = new AgentFilePath("knowledge/files/Doc.txt");
        await fileAccessor.WriteAsync(contentPath, "existing-local-content", CancellationToken.None);
        Assert.True(fileAccessor.Exists(contentPath));

        await Assert.ThrowsAnyAsync<Exception>(() => synchronizer.DownloadKnowledgeFilesAsync(
            workspace,
            CreateStreamingMock(destination => SeedContent(destination, "new-payload")).Object,
            schemaNames: null,
            CancellationToken.None));

        var orphans = fileAccessor.ListFiles()
            .Where(file => file.FileName.EndsWith(".download.tmp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(orphans);
        Assert.Equal("existing-local-content", await fileAccessor.ReadStringAsync(contentPath, CancellationToken.None));
    }

    [Fact]
    public async Task ScanForNewKnowledgeFiles_OrphanedStagingFile_IsNotOfferedAsKnowledgeSource()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/orphaned-staging/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid());
        WriteBotCloudCache(fileAccessor, fileComponent);

        var seeded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace,
            CreateStreamingMock(destination => SeedContent(destination, "real-content")).Object,
            schemaNames: null,
            CancellationToken.None);

        var contentPath = new AgentFilePath(Assert.Single(seeded).RelativePath);

        var orphan = new AgentFilePath($"{contentPath}.deadbeefdeadbeefdeadbeefdeadbeef.download.tmp");
        await fileAccessor.WriteAsync(orphan, "half-written", CancellationToken.None);
        Assert.True(fileAccessor.Exists(orphan));

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var discoveredNames = definition.Components
            .OfType<FileAttachmentComponent>()
            .Select(component => component.DisplayName ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(discoveredNames, name => name.Contains(".download.tmp", StringComparison.OrdinalIgnoreCase));
    }

    private (WorkspaceSynchronizer Synchronizer, DirectoryPath Workspace, string ContentFile) CheckOutKnowledgeFile(byte[] content)
    {
        var files = new FileAccessorFactory();
        var workspace = new DirectoryPath(_root.Replace('\\', '/') + "/");
        WriteBotCloudCache(files.Create(workspace), CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid()));
        var contentFile = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "knowledge", "files")).FullName, "Doc.txt");
        File.WriteAllBytes(contentFile, content);
        File.SetLastWriteTimeUtc(contentFile, CheckedOutOn);

        return (new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), files, new Mock<IIslandControlPlaneService>().Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver()), workspace, contentFile);
    }

    private sealed class AlwaysFailReplaceFactory : IFileAccessorFactory
    {
        private readonly InMemoryFileAccessorFactory _inner = new InMemoryFileAccessorFactory();
        private readonly Dictionary<string, AlwaysFailReplaceAccessor> _wrapped = new Dictionary<string, AlwaysFailReplaceAccessor>(StringComparer.OrdinalIgnoreCase);

        public bool IsMemoryBacked => false;

        public void Release(DirectoryPath root)
        {
        }

        public IFileAccessor Create(DirectoryPath root)
        {
            var key = root.ToString();
            if (!_wrapped.TryGetValue(key, out var accessor))
            {
                accessor = new AlwaysFailReplaceAccessor(_inner.Create(root));
                _wrapped[key] = accessor;
            }

            return accessor;
        }
    }

    private sealed class AlwaysFailReplaceAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;

        public AlwaysFailReplaceAccessor(IFileAccessor inner) => _inner = inner;

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public void Delete(AgentFilePath path) => _inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
            => throw new IOException(SharingViolationMessage, SharingViolationHResult);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
            => _inner.ListFiles(relativeFolder, filePattern);
    }

    private sealed class FailOnFirstReplaceFactory : IFileAccessorFactory
    {
        private readonly InMemoryFileAccessorFactory _inner = new InMemoryFileAccessorFactory();
        private readonly Dictionary<string, FailOnFirstReplaceAccessor> _wrapped = new Dictionary<string, FailOnFirstReplaceAccessor>(StringComparer.OrdinalIgnoreCase);

        public int TotalFailures => _wrapped.Values.Sum(accessor => accessor.FailureCount);

        public bool IsMemoryBacked => false;

        public void Release(DirectoryPath root)
        {
        }

        public IFileAccessor Create(DirectoryPath root)
        {
            var key = root.ToString();
            if (!_wrapped.TryGetValue(key, out var accessor))
            {
                accessor = new FailOnFirstReplaceAccessor(_inner.Create(root));
                _wrapped[key] = accessor;
            }

            return accessor;
        }
    }

    private sealed class FailOnFirstReplaceAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;

        public FailOnFirstReplaceAccessor(IFileAccessor inner) => _inner = inner;

        public int FailureCount { get; private set; }

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public void Delete(AgentFilePath path) => _inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
        {
            if (this.FailureCount == 0)
            {
                this.FailureCount++;
                throw new IOException(SharingViolationMessage, SharingViolationHResult);
            }

            _inner.Replace(sourcePath, targetPath);
        }

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
            => _inner.ListFiles(relativeFolder, filePattern);
    }
}
