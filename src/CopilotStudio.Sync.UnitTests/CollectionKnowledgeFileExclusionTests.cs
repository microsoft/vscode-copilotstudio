// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CollectionKnowledgeFileExclusionTests
{
    private const string AgentSchema = "cre98_AgentB6CC";

    private static FileAttachmentComponent FileComponent(string schemaName, string displayName, Guid? parentCollectionId)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription("desc")
            .ToBuilder();
        builder.Id = Guid.NewGuid();
        if (parentCollectionId.HasValue)
        {
            builder.ParentBotComponentCollectionId = new BotComponentCollectionId(parentCollectionId.Value);
        }
        return builder.Build();
    }

    private static Mock<ISyncDataverseClient> CreateDownloadRecordingDataverse()
    {
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.As<IStreamingKnowledgeFileClient>().Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return mockDataverse;
    }

    [Fact]
    public async Task DownloadKnowledgeFilesAsync_MainAgent_SkipsCollectionOwnedFiles()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-agent-skip/");
        var collectionId = Guid.NewGuid();

        var agentOwnedFile = FileComponent($"{AgentSchema}.file.AgentOwned", "AgentOwned.txt", parentCollectionId: null);
        var collectionOwnedFile = FileComponent($"{AgentSchema}.file.CollectionOwned", "CollectionOwned.txt", parentCollectionId: collectionId);
        var cloudCache = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {AgentSchema}")!)
            .WithComponents(new BotComponentBase[] { agentOwnedFile, collectionOwnedFile });
        WorkspaceSynchronizer.WriteCloudCache(fileAccessorFactory.Create(workspace), cloudCache);

        var mockDataverse = CreateDownloadRecordingDataverse();

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, mockDataverse.Object, schemaNames: null, CancellationToken.None);

        var downloadedFileNames = downloaded.Select(info => info.FileName).ToList();
        Assert.Contains("AgentOwned.txt", downloadedFileNames);
        Assert.DoesNotContain("CollectionOwned.txt", downloadedFileNames);
    }

    [Fact]
    public async Task DownloadKnowledgeFilesAsync_ComponentCollection_DownloadsItsOwnFiles()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-collection-download/");
        var collectionId = Guid.NewGuid();

        var collectionOwnedFile = FileComponent($"{AgentSchema}.file.CollectionOwned", "CollectionOwned.txt", parentCollectionId: collectionId);
        var cloudCache = new BotComponentCollectionDefinition().WithComponents(new BotComponentBase[] { collectionOwnedFile });
        WorkspaceSynchronizer.WriteCloudCache(fileAccessorFactory.Create(workspace), cloudCache);

        var mockDataverse = CreateDownloadRecordingDataverse();

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, mockDataverse.Object, schemaNames: null, CancellationToken.None);

        Assert.Contains("CollectionOwned.txt", downloaded.Select(info => info.FileName).ToList());
    }

    [Fact]
    public async Task ListKnowledgeFilesAsync_MainAgent_ExcludesCollectionOwnedFiles()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-agent-list/");
        var collectionId = Guid.NewGuid();

        var agentOwnedFile = FileComponent($"{AgentSchema}.file.AgentOwned", "AgentOwned.txt", parentCollectionId: null);
        var collectionOwnedFile = FileComponent($"{AgentSchema}.file.CollectionOwned", "CollectionOwned.txt", parentCollectionId: collectionId);
        var cloudCache = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {AgentSchema}")!)
            .WithComponents(new BotComponentBase[] { agentOwnedFile, collectionOwnedFile });
        WorkspaceSynchronizer.WriteCloudCache(fileAccessorFactory.Create(workspace), cloudCache);

        var listed = await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None);

        var schemaNames = listed.Select(info => info.SchemaName).ToList();
        Assert.Contains($"{AgentSchema}.file.AgentOwned", schemaNames);
        Assert.DoesNotContain($"{AgentSchema}.file.CollectionOwned", schemaNames);
    }
}
