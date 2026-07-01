// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Text;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AiModelIdDiscoveryTests
{
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");

    [Fact]
    public void ExtractAiModelIds_FindsModelIdInComponentBody()
    {
        var modelId = Guid.Parse("3b5436b4-d7b4-4389-96e8-107446c9094a");
        var dialogBody =
            "kind: AdaptiveDialog\n" +
            "beginDialog:\n" +
            "  kind: OnUnknownIntent\n" +
            "  actions:\n" +
            "    - kind: SearchAndSummarizeContent\n" +
            "      aIModelId: " + modelId + "\n";
        var definition = MakeDefinitionWithDialog("cr1_topic.topic.Test", dialogBody);

        var ids = WorkspaceSynchronizer.ExtractAiModelIds(definition);

        Assert.Contains(modelId, ids);
    }

    [Fact]
    public void ExtractAiModelIds_NoReference_ReturnsEmpty()
    {
        var dialogBody =
            "kind: AdaptiveDialog\n" +
            "beginDialog:\n" +
            "  kind: OnUnknownIntent\n";
        var definition = MakeDefinitionWithDialog("cr1_topic.topic.Test", dialogBody);

        var ids = WorkspaceSynchronizer.ExtractAiModelIds(definition);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task Clone_WhenComponentReferencesModel_UsesByIdFetchNotServerScan()
    {
        var (synchronizer, _, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/aimodel-clone-byid/");
        var modelId = Guid.Parse("3b5436b4-d7b4-4389-96e8-107446c9094a");

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr123")!;
        var dialogComponent = MakeDialogComponentWithModel("cr1_topic.topic.Test", modelId);
        var changeset = new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(dialogComponent) }, botEntity, "token-1");
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(changeset);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        IReadOnlyCollection<Guid>? capturedIds = null;
        mockDataverse
            .Setup(x => x.DownloadAIPromptsByModelIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<Guid>, CancellationToken>((ids, _) => capturedIds = ids)
            .ReturnsAsync(new[] { new AIPromptMetadata { AIModelId = modelId, Name = "MyPrompt" } });

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), ComponentWriterDefensiveTests.CreateMockOperationContext(), mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.NotNull(capturedIds);
        Assert.Contains(modelId, capturedIds!);
        mockDataverse.Verify(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Clone_WhenNoComponentReferencesModel_FallsBackToServerScan()
    {
        var (synchronizer, _, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/aimodel-clone-fallback/");

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr123")!;
        var changeset = new PvaComponentChangeSet(null, botEntity, "token-1");
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(changeset);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), ComponentWriterDefensiveTests.CreateMockOperationContext(), mockDataverse.Object, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        mockDataverse.Verify(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        mockDataverse.Verify(x => x.DownloadAIPromptsByModelIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static DialogComponent MakeDialogComponentWithModel(string schemaName, Guid modelId)
    {
        var dialogYaml =
            "kind: AdaptiveDialog\n" +
            "beginDialog:\n" +
            "  kind: OnUnknownIntent\n" +
            "  actions:\n" +
            "    - kind: SearchAndSummarizeContent\n" +
            "      aIModelId: " + modelId + "\n";
        var definition = MakeDefinitionWithDialog(schemaName, dialogYaml);
        return definition.Components.OfType<DialogComponent>().Single();
    }

    private static BotDefinition MakeDefinitionWithDialog(string schemaName, string dialogYaml)
    {
        var json = $$"""
        {
          "$kind": "BotDefinition",
          "components": [
            {
              "$kind": "DialogComponent",
              "id": "00000000-0000-0000-0000-000000000001",
              "version": 1,
              "schemaName": {{System.Text.Json.JsonSerializer.Serialize(schemaName)}},
              "dialog": {{System.Text.Json.JsonSerializer.Serialize(dialogYaml)}}
            }
          ]
        }
        """;
        var accessor = new InMemoryFileAccessor(new DirectoryPath($"c:/test/aimodel-{Guid.NewGuid():N}/"));
        var bytes = Encoding.UTF8.GetBytes(json);
        using (var stream = accessor.OpenWrite(CachePath))
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        return (BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
    }
}
