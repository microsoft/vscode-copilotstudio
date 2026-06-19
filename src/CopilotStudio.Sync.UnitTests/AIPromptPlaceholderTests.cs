// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AIPromptPlaceholderTests
{
    [Fact]
    public async Task Clone_WhenAIModelReferenceIsUnreadable_WritesStubDefinitionWithoutPromptFolder()
    {
        var agentId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var unreadableModelId = Guid.Parse("b823f6a0-c344-482f-9cd5-dc3c2e6aa959");

        var workspaceRoot = CreateTempWorkspaceRoot();
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");

        try
        {
            var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var mockDataverse = CreateDataverseMock(new[]
            {
                new AIPromptMetadata
                {
                    AIModelId = unreadableModelId,
                    Name = unreadableModelId.ToString(),
                    IsUnreadableReferencePlaceholder = true,
                },
            });

            SetupClonedBot(mockIsland, agentId);

            await synchronizer.CloneChangesAsync(
                workspace,
                new ReferenceTracker(),
                ComponentWriterDefensiveTests.CreateMockOperationContext(),
                mockDataverse.Object,
                new AgentSyncInfo { AgentId = agentId },
                CancellationToken.None);

            var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

            Assert.Contains(unreadableModelId.ToString(), ReadText(fileAccessor, ".mcs/botdefinition.json"));

            Assert.False(Directory.Exists(Path.Combine(workspaceRoot, "prompts")));
        }
        finally
        {
            DeleteTempWorkspaceRoot(workspaceRoot);
        }
    }

    [Fact]
    public async Task Clone_WithReadableAndUnreadableModels_WritesBothDefinitionsButOnlyReadableModelGetsFolder()
    {
        var agentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var readableModelId = Guid.Parse("c954d507-529b-4d57-88be-02ebcbacce83");
        var unreadableModelId = Guid.Parse("b823f6a0-c344-482f-9cd5-dc3c2e6aa959");

        var workspaceRoot = CreateTempWorkspaceRoot();
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");

        try
        {
            var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

            var mockDataverse = CreateDataverseMock(new[]
            {
                new AIPromptMetadata
                {
                    AIModelId = readableModelId,
                    Name = "ReadableModel",
                    CustomConfiguration = null,
                },
                new AIPromptMetadata
                {
                    AIModelId = unreadableModelId,
                    Name = unreadableModelId.ToString(),
                    IsUnreadableReferencePlaceholder = true,
                },
            });

            SetupClonedBot(mockIsland, agentId);

            await synchronizer.CloneChangesAsync(
                workspace,
                new ReferenceTracker(),
                ComponentWriterDefensiveTests.CreateMockOperationContext(),
                mockDataverse.Object,
                new AgentSyncInfo { AgentId = agentId },
                CancellationToken.None);

            var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
            var botDefinition = ReadText(fileAccessor, ".mcs/botdefinition.json");

            Assert.Contains(readableModelId.ToString(), botDefinition);
            Assert.Contains(unreadableModelId.ToString(), botDefinition);

            var promptsRoot = Path.Combine(workspaceRoot, "prompts");
            var promptFolders = Directory.Exists(promptsRoot)
                ? Directory.GetDirectories(promptsRoot)
                : Array.Empty<string>();

            Assert.Contains(promptFolders, folder => Path.GetFileName(folder).Contains(readableModelId.ToString()));
            Assert.DoesNotContain(promptFolders, folder => Path.GetFileName(folder).Contains(unreadableModelId.ToString()));
        }
        finally
        {
            DeleteTempWorkspaceRoot(workspaceRoot);
        }
    }

    [Fact]
    public async Task Sync_WhenPreviouslyReadableModelBecomesUnreadable_RemovesStaleFolder()
    {
        var agentId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var modelId = Guid.Parse("c954d507-529b-4d57-88be-02ebcbacce83");

        var workspaceRoot = CreateTempWorkspaceRoot();
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");

        try
        {
            var staleFolder = Path.Combine(workspaceRoot, "prompts", $"StaleModel-{modelId}");
            Directory.CreateDirectory(staleFolder);
            File.WriteAllText(Path.Combine(staleFolder, "metadata.yml"), "name: StaleModel");

            var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

            var mockDataverse = CreateDataverseMock(new[]
            {
                new AIPromptMetadata
                {
                    AIModelId = modelId,
                    Name = modelId.ToString(),
                    IsUnreadableReferencePlaceholder = true,
                },
            });

            SetupClonedBot(mockIsland, agentId);

            await synchronizer.CloneChangesAsync(
                workspace,
                new ReferenceTracker(),
                ComponentWriterDefensiveTests.CreateMockOperationContext(),
                mockDataverse.Object,
                new AgentSyncInfo { AgentId = agentId },
                CancellationToken.None);

            Assert.False(Directory.Exists(staleFolder));

            var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
            Assert.Contains(modelId.ToString(), ReadText(fileAccessor, ".mcs/botdefinition.json"));
        }
        finally
        {
            DeleteTempWorkspaceRoot(workspaceRoot);
        }
    }

    [Fact]
    public async Task Push_WhenUnreadableModelExistsInCache_PreservesPlaceholderDefinition()
    {
        var agentId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var readableModelId = Guid.Parse("c954d507-529b-4d57-88be-02ebcbacce83");
        var unreadableModelId = Guid.Parse("b823f6a0-c344-482f-9cd5-dc3c2e6aa959");

        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var workspace = new DirectoryPath("c:/test/push-workspace/");
        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        var seededCache = new BotDefinition().WithAIModelDefinitions(ImmutableArray.Create(
            new AIModelDefinition(id: new AIModelId(readableModelId), name: "ReadableModel", inputType: null, outputType: null),
            new AIModelDefinition(id: new AIModelId(unreadableModelId), name: unreadableModelId.ToString(), inputType: null, outputType: null)));
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, seededCache);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: testbot")!;
        var confirmationChangeset = new PvaComponentChangeSet(null, botEntity, "token-2");
        mockIsland
            .Setup(x => x.SaveChangesAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<PvaComponentChangeSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(confirmationChangeset);

        var diskSourcedPrompts = ImmutableArray.Create(new AIPromptMetadata
        {
            AIModelId = readableModelId,
            Name = "ReadableModel",
        });

        var mockDataverse = new Mock<ISyncDataverseClient>();
        await synchronizer.PushChangesetAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            confirmationChangeset,
            mockDataverse.Object,
            agentId,
            null,
            diskSourcedPrompts,
            CancellationToken.None);

        var botDefinition = ReadText(fileAccessor, ".mcs/botdefinition.json");
        Assert.Contains(unreadableModelId.ToString(), botDefinition);
        Assert.Contains(readableModelId.ToString(), botDefinition);
    }

    private static Mock<ISyncDataverseClient> CreateDataverseMock(AIPromptMetadata[] aiPrompts)
    {
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(aiPrompts);
        return mockDataverse;
    }

    private static void SetupClonedBot(Mock<IIslandControlPlaneService> mockIsland, Guid agentId)
    {
        var clonedBot = new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName("cr123"),
            CdsBotId = agentId,
        }.Build();

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, clonedBot, "token-1"));
    }

    private static string CreateTempWorkspaceRoot()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "aibph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        return workspaceRoot;
    }

    private static void DeleteTempWorkspaceRoot(string workspaceRoot)
    {
        if (Directory.Exists(workspaceRoot))
        {
            Directory.Delete(workspaceRoot, true);
        }
    }

    private static string ReadText(InMemoryFileAccessor fileAccessor, string path)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
