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

public class PullMetadataOnlyChangeTests
{
    private const string Bot = "cr834_n2a8_PwdEI5";
    private const string ToolSchema = "cr834_n2a8_PwdEI5.tool.connected-agent.cr834_n2a8_PwdEI5.action.crf9a_nagentn1_T2U1EY_iL5CJBUv";
    private const string ToolPath = "capabilities/tools/action.crf9a_nagentn1_T2U1EY_iL5CJBUv.mcs.yml";

    [Fact]
    public async Task Pull_RemoteDescriptionOnlyChange_UpdatesWorkspaceFileAndCache()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/pull-metadata-desc-{Guid.NewGuid():N}/");
        var botEntity = CreateCliBotEntity();
        var toolId = Guid.NewGuid();

        SetupChangeset(mockIsland, botEntity, "token-1", CreateConnectedAgentTool(toolId, "NAgent N1", "connect agent n1 local 4", version: 1));

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        Assert.Contains("connect agent n1 local 4", ReadFile(fileAccessor, ToolPath));

        var cachedDefinition = ReadCache(fileAccessor);
        SetupUpdateChangeset(mockIsland, botEntity, "token-2", CreateConnectedAgentTool(toolId, "NAgent N1", "connect agent n1 Cloud 5", version: 2));

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cachedDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileContent = ReadFile(fileAccessor, ToolPath);
        Assert.Contains("connect agent n1 Cloud 5", fileContent);
        Assert.DoesNotContain("connect agent n1 local 4", fileContent);

        var cachedTool = Assert.Single(ReadCache(fileAccessor).Components, c => c.SchemaNameString == ToolSchema);
        Assert.Equal("connect agent n1 Cloud 5", cachedTool.Description);
    }

    [Fact]
    public async Task Pull_RemoteDisplayNameOnlyChange_UpdatesWorkspaceFile()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/pull-metadata-name-{Guid.NewGuid():N}/");
        var botEntity = CreateCliBotEntity();
        var toolId = Guid.NewGuid();

        SetupChangeset(mockIsland, botEntity, "token-1", CreateConnectedAgentTool(toolId, "NAgent N1", "same description", version: 1));

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var cachedDefinition = ReadCache(fileAccessor);
        SetupUpdateChangeset(mockIsland, botEntity, "token-2", CreateConnectedAgentTool(toolId, "NAgent N1 renamed", "same description", version: 2));

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cachedDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.Contains("NAgent N1 renamed", ReadFile(fileAccessor, ToolPath));
    }

    [Fact]
    public async Task Pull_RemoteComponentUnchanged_DoesNotOverwriteLocalEdit()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/pull-metadata-unchanged-{Guid.NewGuid():N}/");
        var botEntity = CreateCliBotEntity();
        var toolId = Guid.NewGuid();
        var cloudTool = CreateConnectedAgentTool(toolId, "NAgent N1", "unchanged", version: 1);

        SetupChangeset(mockIsland, botEntity, "token-1", cloudTool);

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        var localEdit = ReadFile(fileAccessor, ToolPath).Replace("crf9a_nagentn1_T2U1EY", "crf9a_LOCAL_EDIT");
        WriteFile(fileAccessor, ToolPath, localEdit);
        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        SetupUpdateChangeset(mockIsland, botEntity, "token-2", cloudTool);

        await synchronizer.PullExistingChangesAsync(workspace, opContext, localDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        Assert.Contains("crf9a_LOCAL_EDIT", ReadFile(fileAccessor, ToolPath));
    }

    [Fact]
    public async Task Pull_RemoteClearsDescription_WithLocalBodyEdit_DoesNotDiscardLocalEdit()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/pull-metadata-clear-{Guid.NewGuid():N}/");
        var botEntity = CreateCliBotEntity();
        var toolId = Guid.NewGuid();

        SetupChangeset(mockIsland, botEntity, "token-1", CreateConnectedAgentTool(toolId, "NAgent N1", "cloud description", version: 1));

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        var localEdit = ReadFile(fileAccessor, ToolPath).Replace("crf9a_nagentn1_T2U1EY", "crf9a_LOCAL_EDIT");
        WriteFile(fileAccessor, ToolPath, localEdit);
        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        SetupUpdateChangeset(mockIsland, botEntity, "token-2", CreateConnectedAgentTool(toolId, "NAgent N1", string.Empty, version: 2));

        await synchronizer.PullExistingChangesAsync(workspace, opContext, localDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileContent = ReadFile(fileAccessor, ToolPath);
        Assert.Contains("crf9a_LOCAL_EDIT", fileContent);
        Assert.DoesNotContain("cloud description", fileContent);
    }

    private static DialogComponent CreateConnectedAgentTool(Guid id, string displayName, string description, long version, string? schemaName = null)
    {
        var dialog = CodeSerializer.Deserialize<BotElement>(
            "kind: ConnectedAgentTool\nhistoryType:\n  kind: ConversationHistory\nbotSchemaName: crf9a_nagentn1_T2U1EY\n") as DialogBase;
        var builder = new DialogComponent(
            schemaName: schemaName ?? ToolSchema,
            displayName: displayName,
            description: description,
            id: id,
            parentBotComponentId: default,
            dialog: dialog!).ToBuilder();
        builder.Version = version;
        return (DialogComponent)builder.Build();
    }

    private static BotEntity CreateCliBotEntity()
        => CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}\ntemplate: cliagent-1.0.0\n")!;

    private static void SetupChangeset(Mock<IIslandControlPlaneService> mockIsland, BotEntity botEntity, string token, BotComponentBase component)
        => SetupChangeset(mockIsland, botEntity, token, new BotComponentInsert(component));

    private static void SetupUpdateChangeset(Mock<IIslandControlPlaneService> mockIsland, BotEntity botEntity, string token, BotComponentBase component)
        => SetupChangeset(mockIsland, botEntity, token, new BotComponentUpdate(component));

    private static void SetupChangeset(Mock<IIslandControlPlaneService> mockIsland, BotEntity botEntity, string token, BotComponentChange change)
        => mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new[] { change }, botEntity, token));

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

    private static string ReadFile(InMemoryFileAccessor fileAccessor, string path)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteFile(InMemoryFileAccessor fileAccessor, string path, string content)
    {
        using var stream = fileAccessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static DefinitionBase ReadCache(InMemoryFileAccessor fileAccessor)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(".mcs/botdefinition.json"));
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions())!;
        }
    }
}
