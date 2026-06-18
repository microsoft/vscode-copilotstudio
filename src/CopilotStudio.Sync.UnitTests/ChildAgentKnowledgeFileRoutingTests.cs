// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ChildAgentKnowledgeFileRoutingTests
{
    [Fact]
    public void GetComponentPath_ChildAgentFile_ParentMatchesAgentDialog_RoutesToChildFolder()
    {
        var resolver = new LspComponentPathResolver();
        var agentId = new BotComponentId(Guid.NewGuid());
        var agent = CreateAgentDialogComponent("cre98_AgentC1.agent.Agent", agentId.Value);
        var childFile = CreateFileComponent("cre98_AgentC1.file.ChildFile", "ChildFile", agentId);
        var definition = CreateDefinition(new BotComponentBase[] { agent, childFile });

        var path = resolver.GetComponentPath(childFile, definition).Replace('\\', '/');

        Assert.StartsWith("agents/Agent/knowledge/files/", path);
        Assert.EndsWith(".mcs.yml", path);
    }

    [Fact]
    public void GetComponentPath_ChildAgentFile_ParentDoesNotMatchAgentDialog_RoutesToMainFolder()
    {
        var resolver = new LspComponentPathResolver();
        var agent = CreateAgentDialogComponent("cre98_AgentC1.agent.Agent", Guid.NewGuid());
        var childFile = CreateFileComponent("cre98_AgentC1.file.ChildFile", "ChildFile", new BotComponentId(Guid.NewGuid()));
        var definition = CreateDefinition(new BotComponentBase[] { agent, childFile });

        var path = resolver.GetComponentPath(childFile, definition).Replace('\\', '/');

        Assert.StartsWith("knowledge/files/", path);
        Assert.DoesNotContain("agents/", path);
    }

    [Fact]
    public async Task Pull_ChildAgentKnowledgeFile_WritesMetadataToChildFolderNotMain()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-knowledge-pull-{Guid.NewGuid():N}/");

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!;
        var cloudAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent("cre98_AgentC1.agent.Agent", cloudAgentId);
        var cloudChildTopic = CreateTopicComponent("cre98_AgentC1.topic.ChildTopic", Guid.NewGuid(), new BotComponentId(cloudAgentId));

        var cloneChanges = new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(cloudChildTopic),
        };

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(cloneChanges, botEntity, "token-1"));

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());

        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var localAgentId = Guid.NewGuid();
        var localAgent = CreateAgentDialogComponent("cre98_AgentC1.agent.Agent", localAgentId);
        var localChildTopic = CreateTopicComponent("cre98_AgentC1.topic.ChildTopic", Guid.NewGuid(), new BotComponentId(localAgentId));
        var previousDefinition = CreateDefinition(new BotComponentBase[] { localAgent, localChildTopic });

        var childFile = CreateFileComponent("cre98_AgentC1.file.ChildFile", "ChildFile", new BotComponentId(cloudAgentId));
        var pullChanges = new BotComponentChange[]
        {
            new BotComponentInsert(childFile),
        };

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(pullChanges, botEntity, "token-2"));

        await synchronizer.PullExistingChangesAsync(workspace, opContext, previousDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var keys = fileAccessor.Files.Keys.Select(k => k.Replace('\\', '/')).ToList();

        Assert.Contains(keys, k => k.StartsWith("agents/Agent/knowledge/files/", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".mcs.yml", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(keys, k => k.StartsWith("knowledge/files/", StringComparison.OrdinalIgnoreCase));
    }

    private static BotDefinition CreateDefinition(IEnumerable<BotComponentBase> components)
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!;
        return new BotDefinition().WithEntity(botEntity).WithComponents(components);
    }

    private static DialogComponent CreateAgentDialogComponent(string schemaName, Guid id)
    {
        return new DialogComponent(
            schemaName: schemaName,
            displayName: schemaName.Split('.').Last(),
            description: string.Empty,
            id: id,
            parentBotComponentId: default,
            dialog: new AgentDialog());
    }

    private static DialogComponent CreateTopicComponent(string schemaName, Guid id, BotComponentId parentId)
    {
        return new DialogComponent(
            schemaName: schemaName,
            displayName: schemaName.Split('.').Last(),
            description: string.Empty,
            id: id,
            parentBotComponentId: parentId,
            dialog: new AdaptiveDialog());
    }

    private static FileAttachmentComponent CreateFileComponent(string schemaName, string displayName, BotComponentId parentId)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription("desc")
            .ToBuilder();
        builder.Id = Guid.NewGuid();
        builder.ParentBotComponentId = parentId;
        return builder.Build();
    }
}
