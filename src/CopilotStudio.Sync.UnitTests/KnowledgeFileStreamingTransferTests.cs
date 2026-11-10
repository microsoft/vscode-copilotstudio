// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content.Abstractions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.KnowledgeFileTestFixtures;
using ProductionFileAccessorFactory = Microsoft.CopilotStudio.McsCore.InMemoryFileAccessorFactory;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class KnowledgeFileStreamingTransferTests : IDisposable
{
    private readonly ProductionFileAccessorFactory _memoryFactory = new();

    private readonly string _physicalRoot = Path.Combine(Path.GetTempPath(), "mcs-knowledge-streaming-" + Guid.NewGuid().ToString("N"));

    private readonly KnowledgeFileService _service = new();

    public static TheoryData<bool> MemoryBacked => new() { true, false };

    public static TheoryData<bool, HttpStatusCode> Refusals => new()
    {
        { true, HttpStatusCode.Forbidden },
        { true, HttpStatusCode.InternalServerError },
        { false, HttpStatusCode.Forbidden },
        { false, HttpStatusCode.InternalServerError },
    };

    public void Dispose()
    {
        _memoryFactory.Dispose();
        _service.Dispose();

        if (Directory.Exists(_physicalRoot))
        {
            Directory.Delete(_physicalRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(MemoryBacked))]
    public async Task Download_OfAFileSpanningManyChunks_WritesTheExactBytesAndCachesTheirHash(bool memoryBacked)
    {
        var component = CreateFileComponent("cr1.file.Manual", "Manual.pdf", Guid.NewGuid());
        var content = Content(300_001, seed: 1);
        _service.Files[component.Id.Value] = content;
        _service.ChunkSize = 7_919;
        var scenario = Start(memoryBacked, component);

        Assert.Equal(content, await scenario.Accessor.ReadBytesAsync(scenario.PathOf(await scenario.DownloadAsync(), component), CancellationToken.None));
        Assert.Equal(KnowledgeHashOf(content), ReadKnowledgeSyncState(scenario.Accessor)[Key(component)]);
        Assert.DoesNotContain(scenario.Accessor.ListFiles(), IsStagingFile);
    }

    [Theory]
    [MemberData(nameof(MemoryBacked))]
    public async Task Upload_OfAFileSpanningManyChunks_SendsTheExactBytesAndCachesTheCloudHash(bool memoryBacked)
    {
        var component = CreateFileComponent("cr1.file.Manual", "Manual.pdf", Guid.NewGuid());
        _service.Files[component.Id.Value] = Encoding.UTF8.GetBytes("seed");
        var scenario = Start(memoryBacked, component);
        var path = scenario.PathOf(await scenario.DownloadAsync(), component);
        var edited = Content(250_000, seed: 2);
        await scenario.Accessor.WriteAsync(path, edited, CancellationToken.None);
        _service.ChunkSize = 4_096;

        Assert.Equal("Manual.pdf", Assert.Single(await scenario.UploadAsync()));
        Assert.Equal(edited, _service.Files[component.Id.Value]);
        Assert.Equal(KnowledgeHashOf(_service.Files[component.Id.Value]), ReadKnowledgeSyncState(scenario.Accessor)[Key(component)]);
    }

    [Fact]
    public async Task Download_OfAnEmptyFile_WritesAnEmptyFileAndCachesItsHash()
    {
        var component = CreateFileComponent("cr1.file.Empty", "Empty.txt", Guid.NewGuid());
        _service.Files[component.Id.Value] = Array.Empty<byte>();
        var scenario = Start(memoryBacked: true, component);

        var path = scenario.PathOf(await scenario.DownloadAsync(), component);

        Assert.True(scenario.Accessor.Exists(path));
        Assert.Empty(await scenario.Accessor.ReadBytesAsync(path, CancellationToken.None));
        Assert.Equal(KnowledgeHashOf(Array.Empty<byte>()), ReadKnowledgeSyncState(scenario.Accessor)[Key(component)]);
    }

    [Fact]
    public async Task Upload_OfAnEmptiedFile_StoresAnEmptyFileAndCachesItsHash()
    {
        var component = CreateFileComponent("cr1.file.Empty", "Empty.txt", Guid.NewGuid());
        _service.Files[component.Id.Value] = Encoding.UTF8.GetBytes("seed");
        var scenario = Start(memoryBacked: true, component);
        await scenario.Accessor.WriteAsync(scenario.PathOf(await scenario.DownloadAsync(), component), Array.Empty<byte>(), CancellationToken.None);

        Assert.Single(await scenario.UploadAsync());

        Assert.Empty(_service.Files[component.Id.Value]);
        Assert.Equal(KnowledgeHashOf(Array.Empty<byte>()), ReadKnowledgeSyncState(scenario.Accessor)[Key(component)]);
    }

    [Fact]
    public async Task ManyFiles_RoundTripExactlyWithOneRequestPerFileEachWay()
    {
        var components = Enumerable.Range(0, 40).Select(index => CreateFileComponent($"cr1.file.Doc{index}", $"Doc{index}.bin", Guid.NewGuid())).ToArray();
        for (var index = 0; index < components.Length; index++)
        {
            _service.Files[components[index].Id.Value] = Content(index * 1_337, seed: index);
        }

        _service.ChunkSize = 997;
        var scenario = Start(memoryBacked: true, components);

        var downloaded = await scenario.DownloadAsync();

        Assert.Equal(components.Length, downloaded.Length);
        var edited = new Dictionary<Guid, byte[]>();
        foreach (var component in components)
        {
            var path = scenario.PathOf(downloaded, component);
            Assert.Equal(_service.Files[component.Id.Value], await scenario.Accessor.ReadBytesAsync(path, CancellationToken.None));
            edited[component.Id.Value] = Content(4_096 + edited.Count, seed: edited.Count + 100);
            await scenario.Accessor.WriteAsync(path, edited[component.Id.Value], CancellationToken.None);
        }

        var uploaded = await scenario.UploadAsync();

        var cached = ReadKnowledgeSyncState(scenario.Accessor);
        Assert.Equal(components.Length, uploaded.Length);
        Assert.All(components, component => Assert.Equal(edited[component.Id.Value], _service.Files[component.Id.Value]));
        Assert.All(components, component => Assert.Equal(KnowledgeHashOf(_service.Files[component.Id.Value]), cached[Key(component)]));
        Assert.Equal(components.Length, _service.Requests.Count(method => method == HttpMethod.Get));
        Assert.Equal(components.Length, _service.Requests.Count(method => method.Method == "PATCH"));
    }

    [Theory]
    [MemberData(nameof(MemoryBacked))]
    public async Task UnicodeAndNestedNames_RoundTripTheirNamesAndBytes(bool memoryBacked)
    {
        var components = new[] { "Résumé – été.pdf", "日本語のガイド.txt", "emoji 😀 notes.md", "politiques/données ünïcode.docx" }.Select((name, index) => CreateFileComponent($"cr1.file.Unicode{index}", name, Guid.NewGuid())).ToArray();
        foreach (var component in components)
        {
            _service.Files[component.Id.Value] = Encoding.UTF8.GetBytes("cloud " + component.DisplayName);
        }

        var scenario = Start(memoryBacked, components);

        var downloaded = await scenario.DownloadAsync();

        foreach (var component in components)
        {
            var path = scenario.PathOf(downloaded, component);
            Assert.EndsWith(component.DisplayName!, path.ToString(), StringComparison.Ordinal);
            Assert.Equal(_service.Files[component.Id.Value], await scenario.Accessor.ReadBytesAsync(path, CancellationToken.None));
            await scenario.Accessor.WriteAsync(path, Encoding.UTF8.GetBytes("local " + component.DisplayName), CancellationToken.None);
        }

        await scenario.UploadAsync();

        Assert.All(components, component =>
        {
            Assert.Equal(component.DisplayName, _service.FileNames[component.Id.Value]);
            Assert.Equal(Encoding.UTF8.GetBytes("local " + component.DisplayName), _service.Files[component.Id.Value]);
        });
    }

    [Theory]
    [MemberData(nameof(MemoryBacked))]
    public async Task Download_CancelledMidTransfer_KeepsThePreviousContentAndCacheAndLeavesNoStagingFile(bool memoryBacked)
    {
        var component = CreateFileComponent("cr1.file.Manual", "Manual.pdf", Guid.NewGuid());
        _service.Files[component.Id.Value] = Encoding.UTF8.GetBytes("previous");
        var scenario = Start(memoryBacked, component);
        var path = scenario.PathOf(await scenario.DownloadAsync(), component);
        var cachedBefore = ReadKnowledgeSyncState(scenario.Accessor);
        using var cancellation = new CancellationTokenSource();
        _service.Files[component.Id.Value] = Content(200_000, seed: 3);
        _service.ChunkSize = 1_024;
        _service.AfterFirstChunk = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.DownloadAsync(cancellation.Token));

        Assert.Equal(Encoding.UTF8.GetBytes("previous"), await scenario.Accessor.ReadBytesAsync(path, CancellationToken.None));
        Assert.Equal(cachedBefore, ReadKnowledgeSyncState(scenario.Accessor));
        Assert.DoesNotContain(scenario.Accessor.ListFiles(), IsStagingFile);
    }

    [Theory]
    [MemberData(nameof(MemoryBacked))]
    public async Task Upload_CancelledMidTransfer_StoresNothingAndKeepsTheCache(bool memoryBacked)
    {
        var component = CreateFileComponent("cr1.file.Manual", "Manual.pdf", Guid.NewGuid());
        _service.Files[component.Id.Value] = Encoding.UTF8.GetBytes("previous");
        var scenario = Start(memoryBacked, component);
        var path = scenario.PathOf(await scenario.DownloadAsync(), component);
        var edited = Content(200_000, seed: 4);
        await scenario.Accessor.WriteAsync(path, edited, CancellationToken.None);
        var cachedBefore = ReadKnowledgeSyncState(scenario.Accessor);
        using var cancellation = new CancellationTokenSource();
        _service.ChunkSize = 1_024;
        _service.AfterFirstChunk = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scenario.UploadAsync(cancellation.Token));

        Assert.Equal(Encoding.UTF8.GetBytes("previous"), _service.Files[component.Id.Value]);
        Assert.Equal(cachedBefore, ReadKnowledgeSyncState(scenario.Accessor));
        Assert.Equal(edited, await scenario.Accessor.ReadBytesAsync(path, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Download_WhenDataverseRefuses_KeepsThePreviousContentAndCacheAndLeavesNoResidue(bool memoryBacked, HttpStatusCode status)
    {
        var component = CreateFileComponent("cr1.file.Manual", "Manual.pdf", Guid.NewGuid());
        _service.Files[component.Id.Value] = Encoding.UTF8.GetBytes("previous");
        var scenario = Start(memoryBacked, component);
        var path = scenario.PathOf(await scenario.DownloadAsync(), component);
        var filesBefore = scenario.FileList();
        var cachedBefore = ReadKnowledgeSyncState(scenario.Accessor);
        _service.Files[component.Id.Value] = Encoding.UTF8.GetBytes("next");
        _service.FailWith = status;

        Assert.Equal(status, (await Assert.ThrowsAsync<DataverseRequestException>(() => scenario.DownloadAsync())).StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes("previous"), await scenario.Accessor.ReadBytesAsync(path, CancellationToken.None));
        Assert.Equal(filesBefore, scenario.FileList());
        Assert.Equal(cachedBefore, ReadKnowledgeSyncState(scenario.Accessor));
        Assert.Equal(2, _service.Requests.Count);
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Upload_WhenDataverseRefuses_KeepsTheCloudContentAndCacheAndLeavesNoResidue(bool memoryBacked, HttpStatusCode status)
    {
        var component = CreateFileComponent("cr1.file.Manual", "Manual.pdf", Guid.NewGuid());
        _service.Files[component.Id.Value] = Encoding.UTF8.GetBytes("previous");
        var scenario = Start(memoryBacked, component);
        var path = scenario.PathOf(await scenario.DownloadAsync(), component);
        await scenario.Accessor.WriteAsync(path, Encoding.UTF8.GetBytes("edited"), CancellationToken.None);
        var filesBefore = scenario.FileList();
        var cachedBefore = ReadKnowledgeSyncState(scenario.Accessor);
        _service.FailWith = status;

        Assert.Contains($"({(int)status})", (await Assert.ThrowsAsync<InvalidOperationException>(() => scenario.UploadAsync())).Message, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetBytes("previous"), _service.Files[component.Id.Value]);
        Assert.Equal(Encoding.UTF8.GetBytes("edited"), await scenario.Accessor.ReadBytesAsync(path, CancellationToken.None));
        Assert.Equal(filesBefore, scenario.FileList());
        Assert.Equal(cachedBefore, ReadKnowledgeSyncState(scenario.Accessor));
        Assert.Equal(2, _service.Requests.Count);
    }

    private Scenario Start(bool memoryBacked, params FileAttachmentComponent[] components)
    {
        IFileAccessorFactory factory = memoryBacked ? _memoryFactory : new FileAccessorFactory();
        var workspace = memoryBacked ? new DirectoryPath("c:/test/knowledge-streaming/") : new DirectoryPath(_physicalRoot.Replace('\\', '/') + "/");
        var accessor = factory.Create(workspace);
        WriteBotCloudCache(accessor, components);

        var httpClientAccessor = new Mock<IDataverseHttpClientAccessor>();
        httpClientAccessor.Setup(clientAccessor => clientAccessor.CreateClient()).Returns(() => new HttpClient(_service, disposeHandler: false));
        var client = new SyncDataverseClient(httpClientAccessor.Object, "CopilotStudio.Sync", new DataverseRetryPolicy { Delay = (_, _) => Task.CompletedTask });
        client.SetDataverseUrl("https://test.crm.dynamics.com");

        return new Scenario(new WorkspaceSynchronizer(new SyncMcsFileParser(LspProjectorService.Instance), factory, new Mock<IIslandControlPlaneService>().Object, new TestSyncProgress(new List<string>()), new LspComponentPathResolver()), workspace, accessor, client);
    }

    private static byte[] Content(int length, int seed)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static string Key(FileAttachmentComponent component) => component.Id.Value.ToString("N");

    private static bool IsStagingFile(AgentFilePath file) => file.ToString().EndsWith(".download.tmp", StringComparison.Ordinal);

    private sealed record Scenario(WorkspaceSynchronizer Synchronizer, DirectoryPath Workspace, IFileAccessor Accessor, SyncDataverseClient Client)
    {
        public Task<ImmutableArray<KnowledgeFileInfo>> DownloadAsync(CancellationToken cancellationToken = default) => Synchronizer.DownloadKnowledgeFilesAsync(Workspace, Client, schemaNames: null, cancellationToken);

        public Task<ImmutableArray<string>> UploadAsync(CancellationToken cancellationToken = default) => Synchronizer.UploadKnowledgeFilesAsync(Workspace, Client, cancellationToken);

        public AgentFilePath PathOf(ImmutableArray<KnowledgeFileInfo> downloaded, FileAttachmentComponent component) => new(downloaded.Single(info => info.SchemaName == component.SchemaNameString).RelativePath);

        public string[] FileList() => Accessor.ListFiles().Select(file => file.ToString()).OrderBy(file => file, StringComparer.Ordinal).ToArray();
    }

    private sealed class KnowledgeFileService : HttpMessageHandler
    {
        private static readonly Regex FileDataRoute = new(@"/botcomponents\((?<id>[0-9a-fA-F-]{36})\)/filedata/", RegexOptions.CultureInvariant);

        public ConcurrentDictionary<Guid, byte[]> Files { get; } = new();

        public ConcurrentDictionary<Guid, string> FileNames { get; } = new();

        public ConcurrentQueue<HttpMethod> Requests { get; } = new();

        public HttpStatusCode? FailWith { get; set; }

        public int ChunkSize { get; set; } = 81_920;

        public Action? AfterFirstChunk { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var componentId = Guid.Parse(FileDataRoute.Match(request.RequestUri!.AbsolutePath).Groups["id"].Value);
            Requests.Enqueue(request.Method);

            if (FailWith is { } status)
            {
                return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":{\"code\":\"0x80040220\",\"message\":\"refused\"}}") };
            }

            if (request.Method == HttpMethod.Get)
            {
                return Files.TryGetValue(componentId, out var stored)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkedStream(stored, ChunkSize, AfterFirstChunk)) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"message\":\"No file attachment found for attribute: filedata\"}}") };
            }

            using var received = new MemoryStream();
            var body = await request.Content!.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[ChunkSize];
            var chunks = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                received.Write(buffer, 0, read);
                if (++chunks == 1)
                {
                    AfterFirstChunk?.Invoke();
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            Files[componentId] = received.ToArray();
            FileNames[componentId] = request.Headers.GetValues("x-ms-file-name").Single();

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    internal sealed class ChunkedStream(byte[] content, int chunkSize, Action? afterFirstChunk) : Stream
    {
        private int _position;

        private int _chunks;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var delivered = Math.Min(Math.Min(count, chunkSize), content.Length - _position);
            Array.Copy(content, _position, buffer, offset, delivered);
            _position += delivered;

            if (delivered > 0 && ++_chunks == 1)
            {
                afterFirstChunk?.Invoke();
            }

            return delivered;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = new byte[Math.Min(buffer.Length, chunkSize)];
            var delivered = Read(chunk, 0, chunk.Length);
            chunk.AsSpan(0, delivered).CopyTo(buffer.Span);
            return new ValueTask<int>(delivered);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
