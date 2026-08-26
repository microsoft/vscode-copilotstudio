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
}
