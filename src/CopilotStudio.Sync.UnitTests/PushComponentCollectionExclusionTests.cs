// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PushComponentCollectionExclusionTests
{
    private static DialogComponent Topic(string schemaName, string displayName) => new(
        schemaName: schemaName,
        displayName: displayName,
        description: string.Empty,
        id: Guid.NewGuid(),
        parentBotComponentId: default,
        dialog: new AdaptiveDialog());

    private static DialogComponent CollectionOwnedTopic(string schemaName, string displayName, Guid parentCollectionId)
    {
        var builder = Topic(schemaName, displayName).ToBuilder();
        builder.ParentBotComponentCollectionId = new BotComponentCollectionId(parentCollectionId);
        return builder.Build();
    }

    [Fact]
    public void GetLocalChanges_RemoteChange_ExcludesCollectionOwnedComponentsMissingFromCache()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/cc-remote/"));

        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!;
        var collectionOwnedTopic = CollectionOwnedTopic("cre98_AgentC1.topic.Shared", "Shared", Guid.NewGuid());
        var appliedDefinition = new BotDefinition()
            .WithEntity(entity)
            .WithComponents(new[] { Topic("cre98_AgentC1.topic.NewAgentOwned", "NewAgentOwned"), collectionOwnedTopic });
        var cloudSnapshot = new BotDefinition().WithEntity(entity);

        var (changeSet, _) = synchronizer.GetLocalChanges(appliedDefinition, cloudSnapshot, fileAccessor, "token-1", isRemoteChange: true);

        var inserts = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Select(insert => insert.Component!.SchemaNameString).ToList();
        Assert.Contains("cre98_AgentC1.topic.NewAgentOwned", inserts);
        Assert.DoesNotContain("cre98_AgentC1.topic.Shared", inserts);
    }

    [Fact]
    public void GetLocalChanges_WithCollectionOwnedSchemaNames_SkipsThoseComponents()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/cc-skip/"));

        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!;
        var localDefinition = new BotDefinition()
            .WithEntity(entity)
            .WithComponents(new[] { Topic("cre98_AgentC1.topic.Own", "Own"), Topic("cre98_AgentC1.topic.Shared", "Shared") });
        var cloudSnapshot = new BotDefinition().WithEntity(entity);

        var collectionOwned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cre98_AgentC1.topic.Shared" };
        var (changeSet, _) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1", isRemoteChange: false, deferMissingParents: false, out _, collectionOwned);

        var inserts = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Select(insert => insert.Component!.SchemaNameString).ToList();
        Assert.Contains("cre98_AgentC1.topic.Own", inserts);
        Assert.DoesNotContain("cre98_AgentC1.topic.Shared", inserts);
    }

    [Fact]
    public async Task GetLocalChanges_AgentReferencesCollection_ExcludesCollectionOwnedComponentsFromPush()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var agentFolder = new DirectoryPath("c:/test/cc-exclude/Agent/");
        var collectionFolder = agentFolder.ResolveRelativeRef(new RelativeDirectoryPath("../Collection/"));
        var agentId = Guid.NewGuid();

        var sharedComponent = Topic("cre98_AgentC1.topic.Shared", "Shared");
        var collectionAccessor = fileAccessorFactory.Create(collectionFolder);
        var collection = CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_cc\ndisplayName: CC")!;
        WorkspaceSynchronizer.WriteCloudCache(collectionAccessor, new BotComponentCollectionDefinition().WithComponentCollection(collection).WithComponents(new[] { sharedComponent }));
        await collectionAccessor.WriteAsync(new AgentFilePath("topics/cre98_AgentC1.topic.Shared.mcs.yml"), "mcs.metadata:\n  componentName: Shared\nkind: AdaptiveDialog\n", CancellationToken.None);

        var cloudEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!.ToBuilder();
        cloudEntity.CdsBotId = agentId;
        var agentAccessor = fileAccessorFactory.Create(agentFolder);
        WorkspaceSynchronizer.WriteCloudCache(agentAccessor, new BotDefinition().WithEntity(cloudEntity.Build()));
        await agentAccessor.WriteAsync(new AgentFilePath("references.mcs.yml"), "componentCollections:\n  - schemaName:\n    directory: ../Collection/\n", CancellationToken.None);

        var localDefinition = new BotDefinition()
            .WithEntity(cloudEntity.Build())
            .WithComponents(new[] { Topic("cre98_AgentC1.topic.Own", "Own"), sharedComponent });

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(agentFolder, localDefinition, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = agentId }, CancellationToken.None);

        var inserts = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Select(insert => insert.Component!.SchemaNameString).ToList();
        Assert.Contains("cre98_AgentC1.topic.Own", inserts);
        Assert.DoesNotContain("cre98_AgentC1.topic.Shared", inserts);
    }

    [Fact]
    public async Task GetLocalChanges_AgentReferencesCollection_ExcludesLocallyAddedCollectionComponentMissingFromCloudCache()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var agentFolder = new DirectoryPath("c:/test/cc-localadd/Agent/");
        var collectionFolder = agentFolder.ResolveRelativeRef(new RelativeDirectoryPath("../Collection/"));
        var agentId = Guid.NewGuid();

        var collectionAccessor = fileAccessorFactory.Create(collectionFolder);
        var collection = CodeSerializer.Deserialize<BotComponentCollection>("schemaName: bot_componentcollection_cc\ndisplayName: CC")!;
        WorkspaceSynchronizer.WriteCloudCache(collectionAccessor, new BotComponentCollectionDefinition().WithComponentCollection(collection));
        await collectionAccessor.WriteAsync(
            new AgentFilePath("topics/cre98_AgentB4CC.topic.Shared.mcs.yml"),
            "mcs.metadata:\n  componentName: Shared\nkind: AdaptiveDialog\n",
            CancellationToken.None);

        var cloudEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!.ToBuilder();
        cloudEntity.CdsBotId = agentId;
        var agentAccessor = fileAccessorFactory.Create(agentFolder);
        WorkspaceSynchronizer.WriteCloudCache(agentAccessor, new BotDefinition().WithEntity(cloudEntity.Build()));
        await agentAccessor.WriteAsync(new AgentFilePath("references.mcs.yml"), "componentCollections:\n  - schemaName:\n    directory: ../Collection/\n", CancellationToken.None);

        var localDefinition = new BotDefinition()
            .WithEntity(cloudEntity.Build())
            .WithComponents(new[] { Topic("cre98_AgentC1.topic.Own", "Own"), Topic("cre98_AgentB4CC.topic.Shared", "Shared") });

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(agentFolder, localDefinition, new Mock<ISyncDataverseClient>().Object, new AgentSyncInfo { AgentId = agentId }, CancellationToken.None);

        var inserts = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Select(insert => insert.Component!.SchemaNameString).ToList();
        Assert.Contains("cre98_AgentC1.topic.Own", inserts);
        Assert.DoesNotContain("cre98_AgentB4CC.topic.Shared", inserts);
    }

    [Fact]
    public void GetLocalChanges_AgentWithoutCollectionReference_StillInsertsAllComponents()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/cc-none/"));

        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!;
        var localDefinition = new BotDefinition()
            .WithEntity(entity)
            .WithComponents(new[] { Topic("cre98_AgentC1.topic.One", "One"), Topic("cre98_AgentC1.topic.Two", "Two") });
        var cloudSnapshot = new BotDefinition().WithEntity(entity);

        var (changeSet, _) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1", isRemoteChange: false, deferMissingParents: false, out _, null);

        var inserts = changeSet.BotComponentChanges.OfType<BotComponentInsert>().Select(insert => insert.Component!.SchemaNameString).ToList();
        Assert.Contains("cre98_AgentC1.topic.One", inserts);
        Assert.Contains("cre98_AgentC1.topic.Two", inserts);
    }
}
