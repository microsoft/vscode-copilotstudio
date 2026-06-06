// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Text.Json;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CdsBotIdPreservationTests
{
    [Fact]
    public async Task Pull_WhenRemoteBotEntityOmitsCdsBotId_PreservesCdsBotIdInSnapshot()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/agent/");
        var agentId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());

        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = agentId };

        var clonedBot = new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName("cr123"),
            CdsBotId = agentId,
        }.Build();

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, clonedBot, "token-1"));

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        Assert.Contains(agentId.ToString(), ReadText(fileAccessor, ".mcs/botdefinition.json"));

        var previousDefinition = ReadDefinition(fileAccessor);

        var remoteBotWithoutCdsBotId = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr123")!;
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, remoteBotWithoutCdsBotId, "token-2"));

        await synchronizer.PullExistingChangesAsync(workspace, opContext, previousDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.Contains(agentId.ToString(), ReadText(fileAccessor, ".mcs/botdefinition.json"));
    }

    [Fact]
    public async Task Clone_WhenRemoteBotEntityNeverHasCdsBotId_PopulatesCdsBotIdFromAgentId()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/agent/");
        var agentId = Guid.Parse("ad04fd36-f838-f111-88b5-7ced8d3b6119");

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());

        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = agentId };

        // The control-plane Bot entity does not carry cdsBotId (real prompt-agent scenario).
        var remoteBotWithoutCdsBotId = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentB3")!;
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, remoteBotWithoutCdsBotId, "token-1"));

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        var snapshotEntity = ((BotDefinition)ReadDefinition(fileAccessor)).Entity!;
        Assert.Equal(agentId, snapshotEntity.CdsBotId.Value);
    }

    private static string ReadText(InMemoryFileAccessor fileAccessor, string path)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static DefinitionBase ReadDefinition(InMemoryFileAccessor fileAccessor)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(".mcs/botdefinition.json"));
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions())!;
        }
    }
}
