// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
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
}
