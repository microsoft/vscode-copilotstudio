// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class PushConcurrencyRetryTests
{
    private const string AgentSchema = "cre98_AgentC1";

    private static DataversePreconditionFailedException MakeConcurrencyException() =>
        new DataversePreconditionFailedException(
            "0x80060882",
            "ConcurrencyVersionMismatch",
            Guid.NewGuid().ToString(),
            "The version of the existing record doesn't match the RowVersion property provided.",
            null!);

    private static (BotEntity bot, DefinitionBase localDefinition) BuildLocalWithNewTopic(Guid agentId)
    {
        var bot = new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName(AgentSchema),
            CdsBotId = agentId,
        }.Build();

        var topic = new DialogComponent(
            schemaName: $"{AgentSchema}.topic.Greeting",
            displayName: "Greeting",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AdaptiveDialog());

        var localDefinition = new BotDefinition().WithEntity(bot).WithComponents(new[] { topic });
        return (bot, localDefinition);
    }

    [Fact]
    public async Task PushLocalChanges_WhenConcurrencyMismatch_RefreshesCacheAndRetries()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/push-concurrency/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();

        var (bot, localDefinition) = BuildLocalWithNewTopic(agentId);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(bot));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        mockIsland
            .SetupSequence(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeConcurrencyException())
            .ReturnsAsync(new PvaComponentChangeSet(null, bot, "token-3"));

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, bot, "token-2"));

        var syncInfo = new AgentSyncInfo { AgentId = agentId };

        await synchronizer.PushLocalChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            localDefinition,
            new Mock<ISyncDataverseClient>().Object,
            syncInfo,
            cloudFlowMetadata: null,
            aiPrompts: ImmutableArray<AIPromptMetadata>.Empty,
            CancellationToken.None);

        mockIsland.Verify(
            x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        mockIsland.Verify(
            x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // After the refresh (token-2) the retried push succeeds and advances the token to the
        // confirmation changeset's token (token-3).
        Assert.Equal("token-3", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));
    }

    [Fact]
    public async Task PushLocalChanges_WhenConcurrencyMismatchPersists_StopsAfterBoundedRetriesAndThrows()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/push-concurrency-persist/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();

        var (bot, localDefinition) = BuildLocalWithNewTopic(agentId);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(bot));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeConcurrencyException());

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, bot, "token-2"));

        var syncInfo = new AgentSyncInfo { AgentId = agentId };

        await Assert.ThrowsAsync<DataversePreconditionFailedException>(() => synchronizer.PushLocalChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            localDefinition,
            new Mock<ISyncDataverseClient>().Object,
            syncInfo,
            cloudFlowMetadata: null,
            aiPrompts: ImmutableArray<AIPromptMetadata>.Empty,
            CancellationToken.None));

        // Initial attempt + 3 bounded retries.
        mockIsland.Verify(
            x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()),
            Times.Exactly(4));
        mockIsland.Verify(
            x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task PushLocalChanges_WhenRemoteChangedSameComponent_AbortsWithoutOverwrite()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/push-conflict/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var topicId = Guid.NewGuid();

        var bot = new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName(AgentSchema),
            CdsBotId = agentId,
        }.Build();

        var topic = new DialogComponent(
            schemaName: $"{AgentSchema}.topic.Greeting",
            displayName: "Greeting",
            description: string.Empty,
            id: topicId,
            parentBotComponentId: default,
            dialog: new AdaptiveDialog());

        var localDefinition = new BotDefinition().WithEntity(bot).WithComponents(new[] { topic });
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(bot));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeConcurrencyException());

        var remoteTopic = new DialogComponent(
            schemaName: $"{AgentSchema}.topic.Greeting",
            displayName: "Greeting edited by another user",
            description: string.Empty,
            id: topicId,
            parentBotComponentId: default,
            dialog: new AdaptiveDialog());

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(remoteTopic) }, bot, "token-2"));

        var syncInfo = new AgentSyncInfo { AgentId = agentId };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.PushLocalChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            localDefinition,
            new Mock<ISyncDataverseClient>().Object,
            syncInfo,
            cloudFlowMetadata: null,
            aiPrompts: ImmutableArray<AIPromptMetadata>.Empty,
            CancellationToken.None));

        Assert.Contains("Get the latest changes", exception.Message);

        // The conflict abort must NOT advance the local baseline. The change token and cloud
        // cache stay at their pre-push state so a later push cannot silently overwrite the
        // other author's edit without an explicit pull/merge first.
        Assert.Equal("token-1", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));
        var cachedDefinition = await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/botdefinition.json"), CancellationToken.None);
        Assert.DoesNotContain("Greeting edited by another user", cachedDefinition);

        mockIsland.Verify(
            x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockIsland.Verify(
            x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
