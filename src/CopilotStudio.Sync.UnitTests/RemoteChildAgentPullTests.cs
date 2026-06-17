// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class RemoteChildAgentPullTests
{
    [Fact]
    public void GetLocalChanges_RemotePreview_AllowsNewChildComponentUnderNewRemoteAgent()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var cloudSnapshot = CreateDefinition(Array.Empty<BotComponentBase>());
        var agentComponent = CreateDialogComponent("cre98_AgentC1.agent.Agent", Guid.NewGuid(), default, new AgentDialog());
        var childTopic = CreateDialogComponent("cre98_AgentC1.topic.ChildTopic", Guid.NewGuid(), agentComponent.Id, new AdaptiveDialog());
        var appliedRemoteDefinition = CreateDefinition(new[] { agentComponent, childTopic });

        var (_, changes) = synchronizer.GetLocalChanges(appliedRemoteDefinition, cloudSnapshot, fileAccessor, "token-1", isRemoteChange: true);

        Assert.Contains(changes, change => change.SchemaName == agentComponent.SchemaNameString && change.ChangeType == ChangeType.Create);
        Assert.Contains(changes, change => change.SchemaName == childTopic.SchemaNameString && change.ChangeType == ChangeType.Create);
    }

    [Fact]
    public void GetLocalChanges_LocalPush_StillRejectsNewChildComponentUnderNewLocalAgent()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var cloudSnapshot = CreateDefinition(Array.Empty<BotComponentBase>());
        var agentComponent = CreateDialogComponent("cre98_AgentC1.agent.Agent", Guid.NewGuid(), default, new AgentDialog());
        var childTopic = CreateDialogComponent("cre98_AgentC1.topic.ChildTopic", Guid.NewGuid(), agentComponent.Id, new AdaptiveDialog());
        var localDefinition = CreateDefinition(new[] { agentComponent, childTopic });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1"));

        Assert.Contains("ParentId does not exist on cloud: cre98_AgentC1.agent.Agent", exception.Message);
    }

    [Fact]
    public void GetLocalChanges_LocalPush_DeferMissingParents_CreatesNewAgentAndDefersChild()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var cloudSnapshot = CreateDefinition(Array.Empty<BotComponentBase>());
        var agentComponent = CreateDialogComponent("cre98_AgentC1.agent.Agent", Guid.NewGuid(), default, new AgentDialog());
        var childTopic = CreateDialogComponent("cre98_AgentC1.topic.ChildTopic", Guid.NewGuid(), agentComponent.Id, new AdaptiveDialog());
        var localDefinition = CreateDefinition(new[] { agentComponent, childTopic });

        var (_, changes) = synchronizer.GetLocalChanges(localDefinition, cloudSnapshot, fileAccessor, "token-1", isRemoteChange: false, deferMissingParents: true, out var deferredMissingParent);

        Assert.True(deferredMissingParent);
        Assert.Contains(changes, change => change.SchemaName == agentComponent.SchemaNameString && change.ChangeType == ChangeType.Create);
        Assert.DoesNotContain(changes, change => change.SchemaName == childTopic.SchemaNameString);
    }

    [Fact]
    public void GetLocalChanges_RemotePreview_FileComponentWithUnresolvedParent_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workspace/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var cloudSnapshot = CreateDefinition(Array.Empty<BotComponentBase>());
        var orphanedFile = CreateFileComponent("cre98_AgentC1.file.ChildFile", "ChildFile.txt", "desc", new BotComponentId(Guid.NewGuid()));
        var appliedRemoteDefinition = CreateDefinition(new[] { orphanedFile });

        var (_, changes) = synchronizer.GetLocalChanges(appliedRemoteDefinition, cloudSnapshot, fileAccessor, "token-1", isRemoteChange: true);

        Assert.Contains(changes, change => change.SchemaName == orphanedFile.SchemaNameString && change.ChangeType == ChangeType.Create);
    }

    private static BotDefinition CreateDefinition(IEnumerable<BotComponentBase> components)
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cre98_AgentC1")!;
        return new BotDefinition().WithEntity(botEntity).WithComponents(components);
    }

    private static BotComponentBase CreateFileComponent(string schemaName, string displayName, string description, BotComponentId parentId)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription(description)
            .ToBuilder();
        builder.ParentBotComponentId = parentId;
        return builder.Build();
    }

    private static DialogComponent CreateDialogComponent(string schemaName, Guid id, BotComponentId parentId, DialogBase dialog)
    {
        return new DialogComponent(
            schemaName: schemaName,
            displayName: schemaName.Split('.').Last(),
            description: string.Empty,
            id: id,
            parentBotComponentId: parentId,
            dialog: dialog);
    }
}
