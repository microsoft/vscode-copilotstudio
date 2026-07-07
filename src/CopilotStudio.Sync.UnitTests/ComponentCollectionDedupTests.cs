// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ComponentCollectionDedupTests
{
    private const string CollectionSchema = "bot_componentcollection_component_collection_1";

    private static BotComponentCollection Collection() =>
        CodeSerializer.Deserialize<BotComponentCollection>($"schemaName: {CollectionSchema}\ndisplayName: Collection1")!;

    private static BotEntity Entity(Guid agentId)
    {
        var builder = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentB4CC")!.ToBuilder();
        builder.CdsBotId = agentId;
        return builder.Build();
    }

    [Fact]
    public void GetLocalChanges_CloudCacheHasDuplicateComponentCollections_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/cc-dup-cloud/"));
        var entity = Entity(Guid.NewGuid());
        var collection = Collection();

        var cloudSnapshot = new BotDefinition().WithEntity(entity).WithComponentCollections(new[] { collection, collection });
        var localDefinition = new BotDefinition().WithEntity(entity).WithComponentCollections(new[] { collection });

        var (changeSet, _) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1");

        Assert.Empty(changeSet.ComponentCollectionChanges.OfType<BotComponentCollectionDelete>());
    }

    [Fact]
    public void GetLocalChanges_LocalDefinitionHasDuplicateComponentCollections_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/cc-dup-local/"));
        var entity = Entity(Guid.NewGuid());
        var collection = Collection();

        var cloudSnapshot = new BotDefinition().WithEntity(entity).WithComponentCollections(new[] { collection });
        var localDefinition = new BotDefinition().WithEntity(entity).WithComponentCollections(new[] { collection, collection });

        var (changeSet, _) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1");

        Assert.Empty(changeSet.ComponentCollectionChanges);
    }

    [Fact]
    public async Task PullExistingChanges_RemoteReaddsInstalledCollection_DoesNotThrowAndCacheHasSingleCollection()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/cc-pull-dedup/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var entity = Entity(agentId);
        var collection = Collection();

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(entity).WithComponentCollections(new[] { collection }));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        var remoteChangeset = new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[] { new BotComponentCollectionInsert(collection) },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: entity,
            changeToken: "token-2");

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteChangeset);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var previousDefinition = new BotDefinition().WithEntity(entity).WithComponentCollections(new[] { collection });

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previousDefinition, mockDataverse.Object, new AgentSyncInfo { AgentId = agentId }, CancellationToken.None);

        var cache = (BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Single(cache.ComponentCollections.Where(collectionEntry => collectionEntry.SchemaName.Value == CollectionSchema));
    }

    [Fact]
    public async Task PullExistingChanges_CloudInstallsUnreferencedCollection_AppendsSchemaOnlyReferenceEntry()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/cc-outofband/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var entity = Entity(agentId);
        var outOfBandCollection = CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_outofband\ndisplayName: OutOfBand")!;

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(entity));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);
        await fileAccessor.WriteAsync(new AgentFilePath("references.mcs.yml"), "componentCollections:\n  - schemaName:\n    directory: ../ComponentCollection1/\n", CancellationToken.None);

        var remoteChangeset = new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[] { new BotComponentCollectionInsert(outOfBandCollection) },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: entity,
            changeToken: "token-2");

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteChangeset);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var previousDefinition = new BotDefinition().WithEntity(entity);

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previousDefinition, mockDataverse.Object, new AgentSyncInfo { AgentId = agentId }, CancellationToken.None);

        var references = ReadText(fileAccessor, "references.mcs.yml");
        Assert.Contains("bot_componentcollection_outofband", references);
        Assert.Contains("ComponentCollection1", references);
    }

    [Fact]
    public async Task PullExistingChanges_RemoteRemovesReferencedCollection_PrunesStaleReferenceEntry()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/cc-remove/Agent/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var entity = Entity(agentId);
        var collection = Collection().WithId(new BotComponentCollectionId(collectionId));

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(entity).WithComponentCollections(new[] { collection }));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        await fileAccessor.WriteAsync(new AgentFilePath("references.mcs.yml"), "componentCollections:\n  - schemaName:\n    directory: ../Collection1/\n", CancellationToken.None);
        var siblingAccessor = fileAccessorFactory.Create(workspace.ResolveRelativeRef(new RelativeDirectoryPath("../Collection1/")));
        await siblingAccessor.WriteAsync(new AgentFilePath("collection.mcs.yml"), $"schemaName: {CollectionSchema}\ndisplayName: Collection1\n", CancellationToken.None);

        var remoteChangeset = new PvaComponentChangeSet(
            botComponentChanges: null,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: null,
            aIPluginOperationChanges: null,
            componentCollectionChanges: new BotComponentCollectionChange[] { new BotComponentCollectionDelete(collection.Id, collection.Version) },
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: entity,
            changeToken: "token-2");

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteChangeset);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        var previousDefinition = new BotDefinition().WithEntity(entity).WithComponentCollections(new[] { collection });

        await synchronizer.PullExistingChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), previousDefinition, mockDataverse.Object, new AgentSyncInfo { AgentId = agentId }, CancellationToken.None);

        Assert.False(fileAccessor.Exists(new AgentFilePath("references.mcs.yml")));

        var cache = (BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Empty(cache.ComponentCollections.Where(collectionEntry => collectionEntry.SchemaName.Value == CollectionSchema));
    }

    private static string ReadText(IFileAccessor fileAccessor, string path)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
