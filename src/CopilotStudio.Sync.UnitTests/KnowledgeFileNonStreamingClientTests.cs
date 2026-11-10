// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.KnowledgeFileTestFixtures;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class KnowledgeFileNonStreamingClientTests
{
    [Fact]
    public async Task DownloadKnowledgeFile_ClientWithoutStreamingSupport_FallsBackToPathBasedOverload()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-legacy-client/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid());
        WriteBotCloudCache(fileAccessor, fileComponent);

        var nonStreamingClient = new Mock<ISyncDataverseClient>(MockBehavior.Loose);
        nonStreamingClient
            .Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, BotComponentId, string, CancellationToken>((folder, _, fileName, _) =>
            {
                File.WriteAllText(Path.Combine(folder, fileName), "non-streaming-client-payload");
                return Task.CompletedTask;
            });

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace, nonStreamingClient.Object, schemaNames: null, CancellationToken.None);

        var info = Assert.Single(downloaded);
        nonStreamingClient.Verify(
            x => x.DownloadKnowledgeFileAsync(
                It.IsAny<string>(),
                It.Is<BotComponentId>(id => id == fileComponent.Id),
                "Doc.txt",
                It.IsAny<CancellationToken>()),
            Times.Once);

        var contentPath = new AgentFilePath(info.RelativePath);
        Assert.True(fileAccessor.Exists(contentPath));
        Assert.Equal("non-streaming-client-payload", await fileAccessor.ReadStringAsync(contentPath, CancellationToken.None));
    }

    [Fact]
    public async Task UploadKnowledgeFile_ClientWithoutStreamingSupport_ReadsContentFromAccessor()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-legacy-upload/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid());
        WriteBotCloudCache(fileAccessor, fileComponent);

        var seeded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace,
            CreateStreamingMock(destination => SeedContent(destination, "seed")).Object,
            schemaNames: null,
            CancellationToken.None);

        var contentPath = new AgentFilePath(Assert.Single(seeded).RelativePath);
        await fileAccessor.WriteAsync(contentPath, "content-only-in-the-accessor", CancellationToken.None);

        string? observed = null;
        var nonStreamingClient = new Mock<ISyncDataverseClient>(MockBehavior.Loose);
        nonStreamingClient
            .Setup(x => x.UploadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, Guid, string, CancellationToken>((folder, _, fileName, _) =>
            {
                observed = File.ReadAllText(Path.Combine(folder, fileName));
                return Task.CompletedTask;
            });

        var uploaded = await synchronizer.UploadKnowledgeFilesAsync(workspace, nonStreamingClient.Object, CancellationToken.None);

        Assert.Single(uploaded);
        Assert.Equal("content-only-in-the-accessor", observed);
    }

    [Fact]
    public async Task UploadKnowledgeFile_NonStreamingClient_NestedDisplayName_StagesIntoSubdirectory()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/upload-nested-name/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        const string NestedDisplayName = "policies/hr.pdf";
        var fileComponent = CreateFileComponent("cr1.file.Nested", NestedDisplayName, Guid.NewGuid());
        WriteBotCloudCache(fileAccessor, fileComponent);

        var seeded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace,
            CreateStreamingMock(destination => SeedContent(destination, "seed")).Object,
            schemaNames: null,
            CancellationToken.None);

        var contentPath = new AgentFilePath(Assert.Single(seeded).RelativePath);
        await fileAccessor.WriteAsync(contentPath, "nested-payload", CancellationToken.None);

        string? observedContent = null;
        var nonStreamingClient = new Mock<ISyncDataverseClient>(MockBehavior.Loose);
        nonStreamingClient
            .Setup(x => x.UploadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, Guid, string, CancellationToken>((folder, _, fileName, _) =>
            {
                observedContent = File.ReadAllText(Path.Combine(folder, fileName.Replace('/', Path.DirectorySeparatorChar)));
                return Task.CompletedTask;
            });

        var uploaded = await synchronizer.UploadKnowledgeFilesAsync(workspace, nonStreamingClient.Object, CancellationToken.None);

        Assert.Single(uploaded);
        Assert.Equal("nested-payload", observedContent);
    }

    [Fact]
    public async Task ClientWithoutStreamingSupport_RemovesItsStagingFolderOnceEachTransferEnds()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-legacy-cleanup/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        WriteBotCloudCache(fileAccessor, CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid()));

        var stagingFolders = new List<string>();
        var nonStreamingClient = new Mock<ISyncDataverseClient>(MockBehavior.Loose);
        nonStreamingClient
            .Setup(client => client.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, BotComponentId, string, CancellationToken>((folder, _, fileName, _) =>
            {
                stagingFolders.Add(folder);
                File.WriteAllText(Path.Combine(folder, fileName), "downloaded");
                return Task.CompletedTask;
            });
        nonStreamingClient
            .Setup(client => client.UploadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, Guid, string, CancellationToken>((folder, _, _, _) =>
            {
                stagingFolders.Add(folder);
                return Task.CompletedTask;
            });

        await fileAccessor.WriteAsync(new AgentFilePath(Assert.Single(await synchronizer.DownloadKnowledgeFilesAsync(workspace, nonStreamingClient.Object, schemaNames: null, CancellationToken.None)).RelativePath), "edited", CancellationToken.None);
        await synchronizer.UploadKnowledgeFilesAsync(workspace, nonStreamingClient.Object, CancellationToken.None);

        Assert.Equal(2, stagingFolders.Count);
        Assert.All(stagingFolders, folder => Assert.False(Directory.Exists(folder)));
    }

    [Fact]
    public async Task ClientWithoutStreamingSupport_FailedDownload_RemovesItsStagingFolderAndLeavesNoStagedContent()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-legacy-failure/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        WriteBotCloudCache(fileAccessor, CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid()));
        var filesBefore = fileAccessor.ListFiles().Select(file => file.ToString()).ToList();

        string? stagingFolder = null;
        var nonStreamingClient = new Mock<ISyncDataverseClient>(MockBehavior.Loose);
        nonStreamingClient
            .Setup(client => client.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, BotComponentId, string, CancellationToken>((folder, _, fileName, _) =>
            {
                stagingFolder = folder;
                File.WriteAllText(Path.Combine(folder, fileName), "partial");
                throw new DataverseRequestException(System.Net.HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"refused\"}}");
            });

        await Assert.ThrowsAsync<DataverseRequestException>(() => synchronizer.DownloadKnowledgeFilesAsync(workspace, nonStreamingClient.Object, schemaNames: null, CancellationToken.None));

        Assert.NotNull(stagingFolder);
        Assert.False(Directory.Exists(stagingFolder));
        Assert.Equal(filesBefore, fileAccessor.ListFiles().Select(file => file.ToString()).ToList());
    }
}
