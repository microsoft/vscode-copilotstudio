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

public class DefaultAgentMetadataCloneTests
{
    private static readonly AgentFilePath TopAgentPath = new AgentFilePath("agent.mcs.yml");

    [Fact]
    public async Task GetLocalChanges_AfterCloningClassicAgentWithNoGptComponent_ReportsNoChange()
    {
        var (synchronizer, accessor, workspace) = await CloneAsync();

        Assert.True(accessor.Exists(TopAgentPath), "classic clone must still project agent.mcs.yml");

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        Assert.True(
            changes.IsEmpty,
            "A freshly cloned classic agent whose cloud definition has no GptComponent reported "
            + $"{changes.Length} change(s): {string.Join(", ", changes.Select(change => $"{change.ChangeType} {change.SchemaName} -> {change.Uri}"))}");
    }

    [Fact]
    public async Task GetLocalChanges_WhenAgentMetadataFileIsEdited_ReportsTheChange()
    {
        var (synchronizer, accessor, workspace) = await CloneAsync();

        using (var stream = accessor.OpenWrite(TopAgentPath))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write("kind: GptComponentMetadata\ndescription: Authored by the user\n");
        }

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        var change = Assert.Single(changes);
        Assert.Equal(TopAgentPath.ToString(), change.Uri);
    }

    [Fact]
    public async Task PushLocalChanges_AfterCloningClassicAgentWithNoGptComponent_SendsNothing()
    {
        var (synchronizer, _, workspace) = await CloneAsync();

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        Assert.Empty(changeSet.BotComponentChanges);
    }

    [Fact]
    public async Task GetLocalChanges_WhenRootAgentMetadataHasContent_StillReportsTheChange()
    {
        var (synchronizer, accessor, workspace) = await CloneAsync();

        using (var stream = accessor.OpenWrite(TopAgentPath))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write("kind: GptComponentMetadata\ninstructions: Answer questions about orders.\n");
        }

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        Assert.Contains(changes, change => change.Uri == TopAgentPath.ToString());
        Assert.NotEmpty(changeSet.BotComponentChanges);
    }

    [Fact]
    public void GetLocalChanges_RemotePreview_StillReportsEmptyAgentMetadata()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/remote-gpt-preview/"));

        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: catmgr_createtest1")!;
        var cloudSnapshot = new BotDefinition().WithEntity(entity);
        var remoteComponent = new GptComponent()
            .WithSchemaName("catmgr_createtest1.gpt.default")
            .WithDisplayName("default")
            .WithMetadata(new GptComponentMetadata());
        var appliedRemoteDefinition = new BotDefinition().WithEntity(entity).WithComponents(new BotComponentBase[] { remoteComponent });

        var (_, changes) = synchronizer.GetLocalChanges(appliedRemoteDefinition, cloudSnapshot, fileAccessor, "token-1", isRemoteChange: true);

        Assert.Contains(changes, change => change.SchemaName == "catmgr_createtest1.gpt.default" && change.ChangeType == ChangeType.Create);
    }

    private static async Task<(WorkspaceSynchronizer synchronizer, InMemoryFileAccessor accessor, DirectoryPath workspace)> CloneAsync()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/default-agent-metadata-{Guid.NewGuid():N}/");

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), CreateClassicEntity(), "token-1"));

        await synchronizer.CloneChangesAsync(
            workspace,
            new ReferenceTracker(),
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            CreateMockDataverse().Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            CancellationToken.None);

        return (synchronizer, (InMemoryFileAccessor)fileAccessorFactory.Create(workspace), workspace);
    }

    private static BotEntity CreateClassicEntity()
    {
        var json = """
            {
              "$kind": "BotEntity",
              "schemaName": "catmgr_createtest1",
              "displayName": "Create Test 1",
              "accessControlPolicy": "GroupMembership",
              "authenticationMode": "Integrated",
              "authenticationTrigger": "Always",
              "template": "kickStartTemplate-1.0.0",
              "language": 1033,
              "configuration": { "$kind": "BotConfiguration" }
            }
            """;

        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<BotEntity>(json, ElementSerializer.CreateOptions())!;
        }
    }

    private static Mock<ISyncDataverseClient> CreateMockDataverse()
    {
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        return mockDataverse;
    }
}
