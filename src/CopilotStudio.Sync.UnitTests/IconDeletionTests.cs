// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class IconDeletionTests
{
    [Fact]
    public async Task SyncWorkspace_CloudIconRemoved_DeletesStaleIconFile()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/icon-cloud-delete/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var iconPath = new AgentFilePath("icon.png");

        await fileAccessor.WriteAsync(iconPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 }, CancellationToken.None);

        var entityWithoutIcon = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!;
        Assert.Null(entityWithoutIcon.IconBase64);

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(entityWithoutIcon));
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, entityWithoutIcon, "token-1"));

        await synchronizer.SyncWorkspaceAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            null,
            true,
            CreateQuietDataverseMock().Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            cloudFlowMetadata: null,
            CancellationToken.None);

        Assert.False(fileAccessor.Exists(iconPath));
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_LocalIconDeleted_ReportsNullIconInsteadOfCachedIcon()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/icon-local-delete/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var entityBuilder = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!.ToBuilder();
        entityBuilder.IconBase64 = Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var cachedEntityWithIcon = entityBuilder.Build();

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(cachedEntityWithIcon));
        Assert.False(fileAccessor.Exists(new AgentFilePath("icon.png")));

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var botDefinition = Assert.IsType<BotDefinition>(definition);
        Assert.NotNull(botDefinition.Entity);
        Assert.Null(botDefinition.Entity!.IconBase64);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_LocalIconPresent_ReportsThatIcon()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/icon-local-present/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!;
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(entity));

        byte[] iconBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D];
        await fileAccessor.WriteAsync(new AgentFilePath("icon.png"), iconBytes, CancellationToken.None);

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var botDefinition = Assert.IsType<BotDefinition>(definition);
        Assert.Equal(Convert.ToBase64String(iconBytes), botDefinition.Entity!.IconBase64);
    }

    private static Mock<ISyncDataverseClient> CreateQuietDataverseMock()
    {
        var mock = new Mock<ISyncDataverseClient>();
        mock.Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mock.Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        return mock;
    }
}
