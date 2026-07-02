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

/// <summary>
/// Covers cleanup of a sub-agent folder when its child agent is deleted in the cloud and pulled:
/// the hidden .agent.json link is removed and the now-empty folder is pruned, so no orphaned
/// sidecar or empty folder is left behind.
/// </summary>
public class ChildAgentFolderPruneTests
{
    private const string Bot = "cre98_AgentC1";

    [Fact]
    public async Task Pull_ChildAgentDeletedInCloud_RemovesLinkAndPrunesFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-prune-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        // Clone an agent that has one child agent projected under a display-name folder.
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", Guid.NewGuid());
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var keysAfterClone = NormalizedKeys(fileAccessor);
        Assert.Contains("agents/Balance Agent/agent.mcs.yml", keysAfterClone);
        Assert.Contains("agents/Balance Agent/.agent.json", keysAfterClone);

        // The user deletes the child agent in the browser: pull a changeset that removes it.
        var cachedDefinition = ReadCache(fileAccessor);
        var cachedAgent = cachedDefinition.Components.OfType<DialogComponent>().Single(c => c.RootElement is AgentDialog);
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentDelete(cachedAgent.Id, cachedAgent.Version) }, botEntity, "token-2");

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cachedDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        // The agent definition, its hidden link, and the whole (now-empty) folder are gone.
        var keysAfterPull = NormalizedKeys(fileAccessor);
        Assert.DoesNotContain(keysAfterPull, k => k.StartsWith("agents/Balance Agent/", StringComparison.Ordinal));
    }

    [Fact]
    public void DeleteDirectory_RemovesFolderContents_LeavesSiblingsWithSharedPrefix()
    {
        IFileAccessor accessor = new InMemoryFileAccessorFactory().Create(new DirectoryPath("c:/test/deldir/"));
        WriteText(accessor, "agents/Agent/agent.mcs.yml", "x");
        WriteText(accessor, "agents/Agent/.agent.json", "{}");
        WriteText(accessor, "agents/Agent/actions/A.mcs.yml", "x");
        // A sibling whose name shares the "Agent" prefix must NOT be removed.
        WriteText(accessor, "agents/AgentTwo/agent.mcs.yml", "x");

        accessor.DeleteDirectory(new AgentFilePath("agents/Agent"));

        Assert.False(accessor.Exists(new AgentFilePath("agents/Agent/agent.mcs.yml")));
        Assert.False(accessor.Exists(new AgentFilePath("agents/Agent/.agent.json")));
        Assert.False(accessor.Exists(new AgentFilePath("agents/Agent/actions/A.mcs.yml")));
        Assert.True(accessor.Exists(new AgentFilePath("agents/AgentTwo/agent.mcs.yml")));
    }

    [Fact]
    public async Task Pull_ChildAgentWithKnowledgeFileDeletedInCloud_RemovesKnowledgeFileAndPrunesFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-know-prune-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        // Clone an agent with one child agent that owns a knowledge file.
        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var keysAfterClone = NormalizedKeys(fileAccessor);
        Assert.Contains("agents/Balance Agent/agent.mcs.yml", keysAfterClone);
        Assert.Contains(keysAfterClone, k => k.StartsWith("agents/Balance Agent/knowledge/files/", StringComparison.Ordinal) && k.EndsWith(".mcs.yml", StringComparison.Ordinal));

        // The user deletes the child agent (and its knowledge file) in the browser, then pulls.
        var cachedDefinition = ReadCache(fileAccessor);
        var cachedAgent = cachedDefinition.Components.OfType<DialogComponent>().Single(c => c.RootElement is AgentDialog);
        var cachedFile = cachedDefinition.Components.OfType<FileAttachmentComponent>().Single();
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentDelete(cachedAgent.Id, cachedAgent.Version),
            new BotComponentDelete(cachedFile.Id, cachedFile.Version),
        }, botEntity, "token-2");

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cachedDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        // The knowledge file (projected under the child folder) must be deleted too, so the
        // whole sub-agent folder is pruned - nothing is left behind under it.
        var keysAfterPull = NormalizedKeys(fileAccessor);
        Assert.DoesNotContain(keysAfterPull, k => k.StartsWith("agents/Balance Agent/", StringComparison.Ordinal));
    }

    private static void SetupIslandChangeset(Mock<IIslandControlPlaneService> mockIsland, BotComponentChange[] changes, BotEntity botEntity, string token)
        => mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(changes, botEntity, token));

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

    private static DialogComponent CreateAgentDialogComponent(string schemaName, string displayName, Guid id)
        => new(
            schemaName: schemaName,
            displayName: displayName,
            description: string.Empty,
            id: id,
            parentBotComponentId: default,
            dialog: new AgentDialog());

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

    private static List<string> NormalizedKeys(InMemoryFileAccessor fileAccessor)
        => fileAccessor.Files.Keys.Select(k => k.Replace('\\', '/')).ToList();

    private static DefinitionBase ReadCache(InMemoryFileAccessor fileAccessor)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(".mcs/botdefinition.json"));
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions())!;
        }
    }

    private static void WriteText(IFileAccessor accessor, string path, string contents)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream);
        writer.Write(contents);
    }
}
