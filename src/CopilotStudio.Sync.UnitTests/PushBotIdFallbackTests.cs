// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Microsoft.Agents.Platform.Content;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PushBotIdFallbackTests
{
    [Fact]
    public void GetLocalChanges_LocalEntityMissingCdsBotId_InsertUsesCloudSnapshotCdsBotId()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var targetAgentId = Guid.NewGuid();
        var cloudEntityBuilder = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!.ToBuilder();
        cloudEntityBuilder.CdsBotId = targetAgentId;
        var cloudSnapshot = new BotDefinition().WithEntity(cloudEntityBuilder.Build());

        var localEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!;
        var topic = new DialogComponent(
            schemaName: "cre98_AgentC1.topic.Greeting",
            displayName: "Greeting",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AdaptiveDialog());
        var localDefinition = new BotDefinition().WithEntity(localEntity).WithComponents(new[] { topic });

        var (changeSet, _) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1");

        var insert = Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentInsert>());
        Assert.Equal(targetAgentId, insert.Component!.ParentBotId.Value);
    }

    [Fact]
    public void GetLocalChanges_LocalEntityHasStaleCdsBotId_InsertUsesCloudSnapshotCdsBotId()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace-retarget/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var sourceAgentId = Guid.NewGuid();
        var targetAgentId = Guid.NewGuid();

        var cloudEntityBuilder = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!.ToBuilder();
        cloudEntityBuilder.CdsBotId = targetAgentId;
        var cloudSnapshot = new BotDefinition().WithEntity(cloudEntityBuilder.Build());

        var localEntityBuilder = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!.ToBuilder();
        localEntityBuilder.CdsBotId = sourceAgentId;
        var topic = new DialogComponent(
            schemaName: "cre98_AgentC1.topic.Greeting",
            displayName: "Greeting",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AdaptiveDialog());
        var localDefinition = new BotDefinition().WithEntity(localEntityBuilder.Build()).WithComponents(new[] { topic });

        var (changeSet, _) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1");

        var insert = Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentInsert>());
        Assert.Equal(targetAgentId, insert.Component!.ParentBotId.Value);
        Assert.NotEqual(sourceAgentId, insert.Component!.ParentBotId.Value);
    }

    [Fact]
    public void GetLocalChanges_LocalAgentReferencesCollection_EmitsComponentCollectionInsert()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/workspace-collection-link/"));
        var localDefinition = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!)
            .WithComponentCollections(new[] { CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_my_cc_333\ndisplayName: MyCC333")! });
        var cloudSnapshot = new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!);

        var (changeSet, changes) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1");

        Assert.Single(changeSet.ComponentCollectionChanges.OfType<BotComponentCollectionInsert>());
        Assert.Contains(changes, change => change.ChangeType == ChangeType.Create && change.SchemaName == "bot_componentcollection_my_cc_333");
    }

    [Fact]
    public void GetLocalChanges_ReferencedCollectionContentDiffersFromCloud_DoesNotEmitContentChange()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/workspace-collection-noupdate/"));
        var localDefinition = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!)
            .WithComponentCollections(new[] { CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_my_cc_333\ndisplayName: LocallyEditedName")! });
        var cloudSnapshot = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!)
            .WithComponentCollections(new[] { CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_my_cc_333\ndisplayName: CloudName")! });

        var (changeSet, changes) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1");

        Assert.True(changeSet.ComponentCollectionChanges.IsDefaultOrEmpty);
        Assert.DoesNotContain(changes, change => change.ChangeKind == nameof(BotComponentCollection));
    }

    [Fact]
    public async Task PushChangeset_ComponentCollectionInsert_InstallsExistingCollectionWithoutContentSave()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace-collection-install/");
        var agentId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<ISyncComponentCollectionDataverseClient>().Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_my_cc_333", It.IsAny<CancellationToken>())).ReturnsAsync(collectionId);
        dataverse.As<ISyncComponentCollectionDataverseClient>().Setup(client => client.InstallComponentCollectionOnAgentAsync(agentId, collectionId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessorFactory.Create(workspace), new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!));

        await synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[] { new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_my_cc_333\ndisplayName: MyCC333")!) },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: null,
            changeToken: "token-1"), dataverse.Object, agentId, null, default, CancellationToken.None);

        dataverse.As<ISyncComponentCollectionDataverseClient>().Verify(client => client.InstallComponentCollectionOnAgentAsync(agentId, collectionId, It.IsAny<CancellationToken>()), Times.Once);
        mockIsland.Verify(island => island.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PushChangeset_ComponentCollectionDeleteWithEmptyCloudId_FailsClosedWithoutUninstalling()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace-collection-empty-delete/");
        var agentId = Guid.NewGuid();
        var dataverse = new Mock<ISyncDataverseClient>();
        var componentCollectionDataverse = dataverse.As<ISyncComponentCollectionDataverseClient>();
        componentCollectionDataverse.Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_a", It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        WorkspaceSynchronizer.WriteCloudCache(fileAccessorFactory.Create(workspace), new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!));

        var changeset = new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[]
            {
                new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_a\ndisplayName: A")!),
                new BotComponentCollectionDelete(new BotComponentCollectionId(Guid.Empty), 1),
            },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: null,
            changeToken: "token-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), changeset, dataverse.Object, agentId, null, default, CancellationToken.None));

        componentCollectionDataverse.Verify(client => client.InstallComponentCollectionOnAgentAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        componentCollectionDataverse.Verify(client => client.UninstallComponentCollectionFromAgentAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(((BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessorFactory.Create(workspace))!).ComponentCollections);
    }

    [Fact]
    public async Task PushChangeset_MultipleCollectionInserts_ResolveFailureAbortsBeforeAnyInstall()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace-collection-preflight/");
        var agentId = Guid.NewGuid();
        var dataverse = new Mock<ISyncDataverseClient>();
        var componentCollectionDataverse = dataverse.As<ISyncComponentCollectionDataverseClient>();
        componentCollectionDataverse.Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_a", It.IsAny<CancellationToken>())).ReturnsAsync(Guid.NewGuid());
        componentCollectionDataverse.Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_b", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("resolve failed"));
        WorkspaceSynchronizer.WriteCloudCache(fileAccessorFactory.Create(workspace), new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!));

        var changeset = new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[]
            {
                new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_a\ndisplayName: A")!),
                new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_b\ndisplayName: B")!),
            },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: null,
            changeToken: "token-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), changeset, dataverse.Object, agentId, null, default, CancellationToken.None));

        componentCollectionDataverse.Verify(client => client.InstallComponentCollectionOnAgentAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PushChangeset_OneCollectionInstallFails_AppliesRemainingInstallsThenThrowsAndDoesNotWriteCloudCache()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace-collection-besteffort/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var collectionIdA = Guid.NewGuid();
        var collectionIdB = Guid.NewGuid();
        var dataverse = new Mock<ISyncDataverseClient>();
        var componentCollectionDataverse = dataverse.As<ISyncComponentCollectionDataverseClient>();
        componentCollectionDataverse.Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_a", It.IsAny<CancellationToken>())).ReturnsAsync(collectionIdA);
        componentCollectionDataverse.Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_b", It.IsAny<CancellationToken>())).ReturnsAsync(collectionIdB);
        componentCollectionDataverse.Setup(client => client.InstallComponentCollectionOnAgentAsync(agentId, collectionIdA, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("install failed"));
        componentCollectionDataverse.Setup(client => client.InstallComponentCollectionOnAgentAsync(agentId, collectionIdB, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!));

        var changeset = new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[]
            {
                new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_a\ndisplayName: A")!),
                new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_b\ndisplayName: B")!),
            },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: null,
            changeToken: "token-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), changeset, dataverse.Object, agentId, null, default, CancellationToken.None));

        componentCollectionDataverse.Verify(client => client.InstallComponentCollectionOnAgentAsync(agentId, collectionIdB, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Empty(((BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!).ComponentCollections);
    }

    [Fact]
    public async Task PushChangeset_ContentSavedThenLinkMutationFails_PersistsSavedContentToCloudCacheBeforeThrowing()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace-collection-mixed-failure/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var dataverse = new Mock<ISyncDataverseClient>();
        var componentCollectionDataverse = dataverse.As<ISyncComponentCollectionDataverseClient>();
        componentCollectionDataverse.Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_a", It.IsAny<CancellationToken>())).ReturnsAsync(collectionId);
        componentCollectionDataverse.Setup(client => client.InstallComponentCollectionOnAgentAsync(agentId, collectionId, It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("install failed"));
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);
        mockIsland.Setup(island => island.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(null, CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!, "token-2"));

        var changeset = new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[]
            {
                new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_a\ndisplayName: A")!),
            },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!,
            changeToken: "token-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), changeset, dataverse.Object, agentId, null, default, CancellationToken.None));

        Assert.Equal("token-2", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));
        Assert.True(fileAccessor.Exists(new AgentFilePath("settings.mcs.yml")));
        Assert.Empty(((BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!).ComponentCollections);
    }

    [Fact]
    public async Task PushChangeset_ComponentCollectionInsert_CloudCacheCarriesResolvedCloudIdNotEmptyId()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace-collection-cache-id/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<ISyncComponentCollectionDataverseClient>().Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_my_cc_333", It.IsAny<CancellationToken>())).ReturnsAsync(collectionId);
        dataverse.As<ISyncComponentCollectionDataverseClient>().Setup(client => client.InstallComponentCollectionOnAgentAsync(agentId, collectionId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!));

        await synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[] { new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_my_cc_333\ndisplayName: MyCC333")!) },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: null,
            changeToken: "token-1"), dataverse.Object, agentId, null, default, CancellationToken.None);

        var cachedCollection = Assert.Single(((BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!).ComponentCollections);
        Assert.Equal(collectionId, cachedCollection.Id.Value);
        Assert.NotEqual(Guid.Empty, cachedCollection.Id.Value);
        Assert.NotEqual("MyCC333", cachedCollection.DisplayName);
    }

    [Fact]
    public async Task PushChangeset_ComponentCollectionInsert_PreservesExistingResolvedReferencesFile()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace-collection-refs-preserve/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<ISyncComponentCollectionDataverseClient>().Setup(client => client.GetComponentCollectionIdBySchemaNameAsync("bot_componentcollection_my_cc_333", It.IsAny<CancellationToken>())).ReturnsAsync(collectionId);
        dataverse.As<ISyncComponentCollectionDataverseClient>().Setup(client => client.InstallComponentCollectionOnAgentAsync(agentId, collectionId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!));

        var resolvedReferences = "componentCollections:\n  - schemaName:\n    directory: ../ComponentCollection1/\n";
        await fileAccessor.WriteAsync(new AgentFilePath("references.mcs.yml"), resolvedReferences, CancellationToken.None);

        await synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[] { new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_my_cc_333\ndisplayName: MyCC333")!) },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: null,
            changeToken: "token-1"), dataverse.Object, agentId, null, default, CancellationToken.None);

        using var file = fileAccessor.OpenRead(new AgentFilePath("references.mcs.yml"));
        using var reader = new StreamReader(file);
        var actual = reader.ReadToEnd();
        Assert.Contains("directory: ../ComponentCollection1/", actual);
        Assert.DoesNotContain("bot_componentcollection_my_cc_333", actual);
    }

    [Fact]
    public void GetLocalChanges_CachedCollectionRemovedLocally_EmitsDeleteWithCloudId()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/workspace-collection-remove/"));
        var collectionId = Guid.NewGuid();
        var cachedCollection = CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_my_cc_333\ndisplayName: MyCC333")!.WithId(new BotComponentCollectionId(collectionId));
        var cloudSnapshot = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!)
            .WithComponentCollections(new[] { cachedCollection });
        var localWithoutCollection = new BotDefinition().WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!);

        var (changeSet, _) = synchronizer.GetLocalChanges(localWithoutCollection, cloudSnapshot, fileAccessor, "token-1");

        var delete = Assert.Single(changeSet.ComponentCollectionChanges.OfType<BotComponentCollectionDelete>());
        Assert.Equal(collectionId, delete.BotComponentCollectionId.Value);
        Assert.NotEqual(Guid.Empty, delete.BotComponentCollectionId.Value);
    }

}
