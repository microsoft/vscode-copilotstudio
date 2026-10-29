// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
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

    private static BotEntity BuildBot(Guid agentId, string displayName, long version, DateTime? publishedOn = null) =>
        new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName(AgentSchema),
            CdsBotId = agentId,
            DisplayName = displayName,
            Version = version,
            PublishedOn = publishedOn,
        }.Build();

    private static async Task<(WorkspaceSynchronizer Synchronizer, Mock<IIslandControlPlaneService> Island, DirectoryPath Workspace)> SetupSettingsPushAsync(BotEntity cachedBot, BotEntity remoteBot)
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/push-settings-{Guid.NewGuid():N}/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(cachedBot));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        mockIsland
            .SetupSequence(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeConcurrencyException())
            .ReturnsAsync(new PvaComponentChangeSet(null, remoteBot, "token-3"));

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, remoteBot, "token-2"));

        return (synchronizer, mockIsland, workspace);
    }

    private static Task PushRenamedAgentAsync(WorkspaceSynchronizer synchronizer, DirectoryPath workspace, Guid agentId) =>
        synchronizer.PushLocalChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            new BotDefinition().WithEntity(BuildBot(agentId, "Renamed locally", version: 1)),
            new Mock<ISyncDataverseClient>().Object,
            new AgentSyncInfo { AgentId = agentId },
            cloudFlowMetadata: null,
            aiPrompts: ImmutableArray<AIPromptMetadata>.Empty,
            CancellationToken.None);

    [Fact]
    public async Task PushLocalChanges_WhenTheCloudOnlyPublished_RefreshesCacheAndRetries()
    {
        var agentId = Guid.NewGuid();
        var published = BuildBot(agentId, "Agent", version: 2, publishedOn: new DateTime(2026, 10, 8, 11, 0, 0, DateTimeKind.Utc));

        var (synchronizer, island, workspace) = await SetupSettingsPushAsync(BuildBot(agentId, "Agent", version: 1), published);

        await PushRenamedAgentAsync(synchronizer, workspace, agentId);

        island.Verify(
            x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task PushLocalChanges_WhenTheCloudEditedSettings_RefusesToOverwrite()
    {
        var agentId = Guid.NewGuid();
        var edited = BuildBot(agentId, "Renamed in the cloud", version: 2);

        var (synchronizer, _, workspace) = await SetupSettingsPushAsync(BuildBot(agentId, "Agent", version: 1), edited);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PushRenamedAgentAsync(synchronizer, workspace, agentId));

        Assert.Contains("Get the latest changes", exception.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PushLocalChanges_WhenRemoteDeletesClonedChildDuringUnrelatedPush_PreservesBaseline(bool legacyLink, bool retainOtherChild)
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/push-remote-child-delete-{Guid.NewGuid():N}/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var (bot, localDefinition) = BuildLocalWithNewTopic(agentId);
        var childAgent = CreateChildAgent("Existing", "Friendly");
        var cachedComponents = new List<BotComponentBase> { childAgent };
        if (retainOtherChild)
        {
            var otherAgent = CreateChildAgent("Other", "Other");
            cachedComponents.Add(otherAgent);
            await WriteChildAgentLinkAsync(fileAccessor, otherAgent, legacyLink: false);
        }
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(bot).WithComponents(cachedComponents));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);
        await WriteChildAgentLinkAsync(fileAccessor, childAgent, legacyLink);
        localDefinition = localDefinition.WithComponents(localDefinition.Components.Concat(cachedComponents.Select(c => c.WithId(Guid.NewGuid()))));

        var cacheBefore = await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/botdefinition.json"), CancellationToken.None);
        var anchorPath = new AgentFilePath("agents/Friendly/agent.mcs.yml");
        var anchorBefore = await fileAccessor.ReadStringAsync(anchorPath, CancellationToken.None);
        var linkPath = new AgentFilePath("agents/Friendly/.agent.json");
        var linkBefore = legacyLink ? await fileAccessor.ReadStringAsync(linkPath, CancellationToken.None) : null;
        var sentChangesets = new List<PvaComponentChangeSet>();
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentDelete(childAgent.Id, childAgent.Version) }, null, "token-2"));

        for (var push = 0; push < 2; push++)
        {
            var attempts = 0;
            mockIsland
                .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
                .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
                {
                    sentChangesets.Add(incoming);
                    if (attempts++ == 0)
                    {
                        throw MakeConcurrencyException();
                    }
                    return Task.FromResult(new PvaComponentChangeSet(incoming.BotComponentChanges, incoming.Bot, "token-3"));
                });

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.PushLocalChangesAsync(
                workspace,
                ComponentWriterDefensiveTests.CreateMockOperationContext(),
                localDefinition,
                new Mock<ISyncDataverseClient>().Object,
                new AgentSyncInfo { AgentId = agentId },
                cloudFlowMetadata: null,
                aiPrompts: ImmutableArray<AIPromptMetadata>.Empty,
                CancellationToken.None));

            Assert.Contains("Get the latest changes", exception.Message);
            Assert.Equal(1, attempts);
            Assert.Equal(cacheBefore, await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/botdefinition.json"), CancellationToken.None));
            Assert.Equal("token-1", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));
            Assert.Equal(anchorBefore, await fileAccessor.ReadStringAsync(anchorPath, CancellationToken.None));
            if (legacyLink)
            {
                Assert.Equal(linkBefore, await fileAccessor.ReadStringAsync(linkPath, CancellationToken.None));
            }
        }

        Assert.Equal(2, sentChangesets.Count);
        Assert.All(sentChangesets, changes =>
        {
            var insert = Assert.IsType<BotComponentInsert>(Assert.Single(changes.BotComponentChanges));
            Assert.Equal($"{AgentSchema}.topic.Greeting", insert.Component!.SchemaNameString);
        });
        mockIsland.Verify(
            x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), "token-1", It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PushLocalChanges_WhenNewChildReplacesOldChild_StillRetriesNonconflictingRefresh(bool legacyLink)
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/push-new-child-retry-{Guid.NewGuid():N}/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();
        var (bot, localDefinition) = BuildLocalWithNewTopic(agentId);
        var previousAgent = CreateChildAgent("Previous", "Previous");
        var newAgent = CreateChildAgent("Refunds", "Refunds");
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(bot).WithComponents(new[] { previousAgent }));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);
        await WriteChildAgentLinkAsync(fileAccessor, newAgent, legacyLink);
        localDefinition = localDefinition.WithComponents(new[] { newAgent });

        var cacheBefore = await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/botdefinition.json"), CancellationToken.None);
        var serverAgentId = Guid.NewGuid();
        var sentChangesets = new List<PvaComponentChangeSet>();
        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>(async (_, incoming, _) =>
            {
                sentChangesets.Add(incoming);
                Assert.Equal(cacheBefore, await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/botdefinition.json"), CancellationToken.None));
                if (sentChangesets.Count == 1)
                {
                    throw MakeConcurrencyException();
                }
                var confirmed = incoming.BotComponentChanges.Select(change =>
                    change is BotComponentInsert insert
                        ? new BotComponentInsert(insert.Component!.WithId(serverAgentId))
                        : change);
                return new PvaComponentChangeSet(confirmed, incoming.Bot, "token-3");
            });
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, null, "token-2"));

        await synchronizer.PushLocalChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            localDefinition,
            new Mock<ISyncDataverseClient>().Object,
            new AgentSyncInfo { AgentId = agentId },
            cloudFlowMetadata: null,
            aiPrompts: ImmutableArray<AIPromptMetadata>.Empty,
            CancellationToken.None);

        Assert.Equal(2, sentChangesets.Count);
        Assert.All(sentChangesets, changes =>
        {
            Assert.Equal(newAgent.SchemaNameString, Assert.Single(changes.BotComponentChanges.OfType<BotComponentInsert>()).Component!.SchemaNameString);
            Assert.Equal(previousAgent.Id, Assert.Single(changes.BotComponentChanges.OfType<BotComponentDelete>()).BotComponentId);
            Assert.Equal(2, changes.BotComponentChanges.Length);
        });
        var confirmedAgent = Assert.Single(WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!.Components);
        Assert.Equal(newAgent.SchemaNameString, confirmedAgent.SchemaNameString);
        Assert.Equal(serverAgentId, confirmedAgent.Id.Value);
        Assert.Equal("token-3", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));
        mockIsland.Verify(
            x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), "token-1", It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Fact]
    public async Task PushLocalChanges_WhenRemoteChangedAgentSettingsOrIcon_AbortsWithoutOverwrite()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/push-bot-conflict/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        var agentId = Guid.NewGuid();

        var cloudBot = new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName(AgentSchema),
            CdsBotId = agentId,
            Version = 100,
        }.Build();

        var localBot = new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName(AgentSchema),
            CdsBotId = agentId,
            Version = 100,
            IconBase64 = "bG9jYWxJY29u",
        }.Build();
        var localDefinition = new BotDefinition().WithEntity(localBot);

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(cloudBot));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(MakeConcurrencyException());

        var remoteBot = new BotEntity.Builder
        {
            SchemaName = new BotEntitySchemaName(AgentSchema),
            CdsBotId = agentId,
            Version = 101,
            IconBase64 = "cmVtb3RlSWNvbg==",
        }.Build();

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, remoteBot, "token-2"));

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

        Assert.Equal("token-1", await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/changetoken.txt"), CancellationToken.None));

        mockIsland.Verify(
            x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()),
            Times.Once);
        mockIsland.Verify(
            x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PushLocalChanges_WhenRetryingAfterARefresh_KeepsAComponentChangedOnlyInTheCloud()
    {
        var agentId = Guid.NewGuid();
        var farewellId = Guid.NewGuid();
        var farewell = CreateTopic("Farewell", "farewellOriginal", farewellId);

        var (fileAccessor, sentChangesets) = await PushAfterRemoteRefreshAsync(
            new BotDefinition().WithEntity(BuildBot(agentId, "Agent", version: 1)).WithComponents(new[] { farewell }),
            new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentUpdate(CreateTopic("Farewell", "farewellCloud", farewellId)) }, BuildBot(agentId, "Agent", version: 2, publishedOn: new DateTime(2026, 10, 8, 11, 0, 0, DateTimeKind.Utc)), "token-2"),
            new BotDefinition().WithEntity(BuildBot(agentId, "Renamed locally", version: 1)).WithComponents(new[] { farewell }));

        Assert.Equal(2, sentChangesets.Count);
        Assert.Equal("Renamed locally", sentChangesets[1].Bot!.DisplayName);
        Assert.Empty(sentChangesets[1].BotComponentChanges);
        Assert.Contains("farewellCloud", await ReadAsync(fileAccessor, "topics/Farewell.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushLocalChanges_WhenRetryingAfterARefresh_KeepsAgentSettingsChangedOnlyInTheCloud()
    {
        var agentId = Guid.NewGuid();
        var bot = BuildBot(agentId, "Agent", version: 1);

        var (fileAccessor, sentChangesets) = await PushAfterRemoteRefreshAsync(
            new BotDefinition().WithEntity(bot),
            new PvaComponentChangeSet(null, BuildBot(agentId, "Renamed in the cloud", version: 2), "token-2"),
            new BotDefinition().WithEntity(bot).WithComponents(new[] { CreateTopic("Greeting", "greetingLocal", Guid.NewGuid()) }));

        Assert.Equal(2, sentChangesets.Count);
        Assert.Null(sentChangesets[1].Bot);
        Assert.IsType<BotComponentInsert>(Assert.Single(sentChangesets[1].BotComponentChanges));
        Assert.Contains("Renamed in the cloud", await ReadAsync(fileAccessor, "settings.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushLocalChanges_WithOverwrite_ReplacesTheConflictingComponentAndKeepsTheCloudOnlyOne()
    {
        var bot = BuildBot(Guid.NewGuid(), "Agent", version: 1);
        var greetingId = Guid.NewGuid();
        var farewellId = Guid.NewGuid();
        var farewell = CreateTopic("Farewell", "farewellOriginal", farewellId);

        var (fileAccessor, sentChangesets) = await PushAfterRemoteRefreshAsync(
            new BotDefinition().WithEntity(bot).WithComponents(new[] { CreateTopic("Greeting", "greetingOriginal", greetingId), farewell }),
            new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentUpdate(CreateTopic("Greeting", "greetingCloud", greetingId)), new BotComponentUpdate(CreateTopic("Farewell", "farewellCloud", farewellId)) }, null, "token-2"),
            new BotDefinition().WithEntity(bot).WithComponents(new[] { CreateTopic("Greeting", "greetingLocal", greetingId), farewell }),
            overwriteRemoteChanges: true);

        Assert.Equal(2, sentChangesets.Count);
        Assert.Equal($"{AgentSchema}.topic.Greeting", Assert.IsType<BotComponentUpdate>(Assert.Single(sentChangesets[1].BotComponentChanges)).Component!.SchemaNameString);
        Assert.Contains("greetingLocal", await ReadAsync(fileAccessor, "topics/Greeting.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains("farewellCloud", await ReadAsync(fileAccessor, "topics/Farewell.mcs.yml"), StringComparison.Ordinal);
        var cache = await ReadAsync(fileAccessor, ".mcs/botdefinition.json");
        Assert.Contains("greetingLocal", cache, StringComparison.Ordinal);
        Assert.Contains("farewellCloud", cache, StringComparison.Ordinal);
        Assert.DoesNotContain("greetingCloud", cache, StringComparison.Ordinal);
        Assert.Equal("token-3", await ReadAsync(fileAccessor, ".mcs/changetoken.txt"));
    }

    [Fact]
    public async Task PushLocalChanges_WhenRetryingAfterARefresh_DownloadsAKnowledgeFileAddedOnlyInTheCloud()
    {
        var bot = BuildBot(Guid.NewGuid(), "Agent", version: 1);

        var (fileAccessor, sentChangesets) = await PushAfterRemoteRefreshAsync(
            new BotDefinition().WithEntity(bot),
            new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(KnowledgeFileTestFixtures.CreateFileComponent($"{AgentSchema}.file.Notes", "Notes.txt", Guid.NewGuid())) }, null, "token-2"),
            new BotDefinition().WithEntity(bot).WithComponents(new[] { CreateTopic("Greeting", "greetingLocal", Guid.NewGuid()) }),
            dataverseClient: KnowledgeFileTestFixtures.CreateStreamingMock(destination => KnowledgeFileTestFixtures.SeedContent(destination, "cloud notes")).Object);

        Assert.IsType<BotComponentInsert>(Assert.Single(sentChangesets[1].BotComponentChanges));
        Assert.Equal("cloud notes", await ReadAsync(fileAccessor, "knowledge/files/Notes.txt"));
    }

    [Fact]
    public async Task PushLocalChanges_WhenRetryingAfterARefresh_KeepsEnvironmentVariablesChangedOnlyInTheCloud()
    {
        var bot = BuildBot(Guid.NewGuid(), "Agent", version: 1);
        var endpointId = Guid.NewGuid();
        var endpoint = CreateEnvironmentVariable("Endpoint", "https://original", endpointId);

        var (fileAccessor, sentChangesets) = await PushAfterRemoteRefreshAsync(
            new BotDefinition().WithEntity(bot).WithEnvironmentVariables(new[] { endpoint }),
            new PvaComponentChangeSet.Builder { ChangeToken = "token-2", EnvironmentVariableChanges = { new EnvironmentVariableUpdate(CreateEnvironmentVariable("Endpoint", "https://cloud", endpointId)), new EnvironmentVariableInsert(CreateEnvironmentVariable("Region", "westus", Guid.NewGuid())) } }.Build(),
            new BotDefinition().WithEntity(bot).WithEnvironmentVariables(new[] { endpoint }).WithComponents(new[] { CreateTopic("Greeting", "greetingLocal", Guid.NewGuid()) }));

        Assert.Equal(2, sentChangesets.Count);
        Assert.Empty(sentChangesets[1].EnvironmentVariableChanges);
        Assert.IsType<BotComponentInsert>(Assert.Single(sentChangesets[1].BotComponentChanges));
        Assert.Contains("https://cloud", await ReadAsync(fileAccessor, $"environmentvariables/{AgentSchema}.Endpoint.mcs.yml"), StringComparison.Ordinal);
        Assert.Contains("westus", await ReadAsync(fileAccessor, $"environmentvariables/{AgentSchema}.Region.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushLocalChanges_WhenRemoteChangedTheSameEnvironmentVariable_AbortsWithoutOverwrite()
    {
        var bot = BuildBot(Guid.NewGuid(), "Agent", version: 1);
        var endpointId = Guid.NewGuid();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PushAfterRemoteRefreshAsync(
            new BotDefinition().WithEntity(bot).WithEnvironmentVariables(new[] { CreateEnvironmentVariable("Endpoint", "https://original", endpointId) }),
            new PvaComponentChangeSet.Builder { ChangeToken = "token-2", EnvironmentVariableChanges = { new EnvironmentVariableUpdate(CreateEnvironmentVariable("Endpoint", "https://cloud", endpointId)) } }.Build(),
            new BotDefinition().WithEntity(bot).WithEnvironmentVariables(new[] { CreateEnvironmentVariable("Endpoint", "https://local", endpointId) })));

        Assert.Contains("Get the latest changes", exception.Message);
    }

    [Fact]
    public async Task PushLocalChanges_WithOverwrite_ReplacesTheConflictingEnvironmentVariable()
    {
        var bot = BuildBot(Guid.NewGuid(), "Agent", version: 1);
        var endpointId = Guid.NewGuid();

        var (fileAccessor, sentChangesets) = await PushAfterRemoteRefreshAsync(
            new BotDefinition().WithEntity(bot).WithEnvironmentVariables(new[] { CreateEnvironmentVariable("Endpoint", "https://original", endpointId) }),
            new PvaComponentChangeSet.Builder { ChangeToken = "token-2", EnvironmentVariableChanges = { new EnvironmentVariableUpdate(CreateEnvironmentVariable("Endpoint", "https://cloud", endpointId)) } }.Build(),
            new BotDefinition().WithEntity(bot).WithEnvironmentVariables(new[] { CreateEnvironmentVariable("Endpoint", "https://local", endpointId) }),
            overwriteRemoteChanges: true);

        Assert.Equal(2, sentChangesets.Count);
        Assert.Equal("https://local", Assert.IsType<EnvironmentVariableUpdate>(Assert.Single(sentChangesets[1].EnvironmentVariableChanges)).EnvironmentVariable!.DefaultValue);
        Assert.Contains("https://local", await ReadAsync(fileAccessor, $"environmentvariables/{AgentSchema}.Endpoint.mcs.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PushLocalChanges_WhenRetryingAfterARefresh_KeepsConnectionReferencesChangedInTheCloudOrOnDisk()
    {
        var bot = new BotEntity.Builder { SchemaName = new BotEntitySchemaName(AgentSchema), CdsBotId = Guid.NewGuid(), Template = "cliagent-1.0.0" }.Build();
        var edited = CreateConnectionReference("cre98_edited", "shared_office365");
        var deleted = CreateConnectionReference("cre98_deleted", "shared_office365");
        var cloudDefinition = new BotDefinition().WithEntity(bot).WithConnectionReferences(new[] { edited, deleted });

        var (fileAccessor, sentChangesets) = await PushAfterRemoteRefreshAsync(
            cloudDefinition,
            new PvaComponentChangeSet.Builder { ChangeToken = "token-2", ConnectionReferenceChanges = { new ConnectionReferenceInsert(CreateConnectionReference("cre98_added", "shared_teams")) } }.Build(),
            cloudDefinition,
            workspaceFiles: new Dictionary<string, string>
            {
                ["settings.mcs.yml"] = CodeSerializer.Serialize(bot),
                [GetConnectionReferencePath("cre98_edited")] = "connectionReferences:\n  - connectionReferenceLogicalName: cre98_edited\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_sharepointonline\n",
            });

        Assert.Equal(2, sentChangesets.Count);
        var changes = sentChangesets[1].ConnectionReferenceChanges;
        Assert.Equal(2, changes.Length);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_sharepointonline", Assert.IsType<ConnectionReferenceUpdate>(Assert.Single(changes, change => change is ConnectionReferenceUpsert)).ConnectionReference!.ConnectorId.Value);
        Assert.Equal(deleted.Id.Value, Assert.IsType<ConnectionReferenceDelete>(Assert.Single(changes, change => change is ConnectionReferenceDelete)).ConnectionReferenceId.Value);
        Assert.Contains("shared_sharepointonline", await ReadAsync(fileAccessor, GetConnectionReferencePath("cre98_edited")), StringComparison.Ordinal);
        Assert.Contains("shared_teams", await ReadAsync(fileAccessor, GetConnectionReferencePath("cre98_added")), StringComparison.Ordinal);
        Assert.False(fileAccessor.Exists(new AgentFilePath(GetConnectionReferencePath("cre98_deleted"))));
    }

    [Fact]
    public async Task PushLocalChanges_WhenRetryingAfterARefresh_KeepsAComponentCollectionAddedOnlyInTheCloud()
    {
        var bot = BuildBot(Guid.NewGuid(), "Agent", version: 1);
        var dataverse = new Mock<ISyncDataverseClient>();
        var componentCollections = dataverse.As<ISyncComponentCollectionDataverseClient>();

        var (fileAccessor, sentChangesets) = await PushAfterRemoteRefreshAsync(
            new BotDefinition().WithEntity(bot),
            new PvaComponentChangeSet.Builder { ChangeToken = "token-2", ComponentCollectionChanges = { new BotComponentCollectionInsert(CodeSerializer.Deserialize<BotComponentCollection>("schemaName: cre98_shared\ndisplayName: Shared")!.WithId(new BotComponentCollectionId(Guid.NewGuid()))) } }.Build(),
            new BotDefinition().WithEntity(bot).WithComponents(new[] { CreateTopic("Greeting", "greetingLocal", Guid.NewGuid()) }),
            dataverseClient: dataverse.Object);

        Assert.Equal(2, sentChangesets.Count);
        componentCollections.Verify(client => client.UninstallComponentCollectionFromAgentAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("cre98_shared", await ReadAsync(fileAccessor, "references.mcs.yml"), StringComparison.Ordinal);
    }

    private static async Task<(IFileAccessor FileAccessor, List<PvaComponentChangeSet> SentChangesets)> PushAfterRemoteRefreshAsync(BotDefinition cloudCache, PvaComponentChangeSet remoteChangeset, BotDefinition localDefinition, bool overwriteRemoteChanges = false, ISyncDataverseClient? dataverseClient = null, IReadOnlyDictionary<string, string>? workspaceFiles = null)
    {
        var (synchronizer, fileAccessorFactory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/push-refresh-{Guid.NewGuid():N}/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, cloudCache);
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-1", CancellationToken.None);
        foreach (var environmentVariable in localDefinition.EnvironmentVariables)
        {
            await fileAccessor.WriteAsync(WorkspaceSynchronizer.GetEnvironmentVariablePath(environmentVariable), CodeSerializer.Serialize(environmentVariable), CancellationToken.None);
        }

        foreach (var file in workspaceFiles ?? new Dictionary<string, string>())
        {
            await fileAccessor.WriteAsync(new AgentFilePath(file.Key), file.Value, CancellationToken.None);
        }

        var sentChangesets = new List<PvaComponentChangeSet>();
        island
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                sentChangesets.Add(incoming);
                return sentChangesets.Count == 1 ? throw MakeConcurrencyException() : Task.FromResult(new PvaComponentChangeSet.Builder(incoming) { ChangeToken = "token-3" }.Build());
            });
        island
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(remoteChangeset);

        await synchronizer.PushLocalChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            localDefinition,
            dataverseClient ?? new Mock<ISyncDataverseClient>().Object,
            new AgentSyncInfo { AgentId = localDefinition.Entity!.CdsBotId.Value },
            cloudFlowMetadata: null,
            aiPrompts: ImmutableArray<AIPromptMetadata>.Empty,
            CancellationToken.None,
            overwriteRemoteChanges: overwriteRemoteChanges);

        return (fileAccessor, sentChangesets);
    }

    private static Task<string> ReadAsync(IFileAccessor fileAccessor, string path) => fileAccessor.ReadStringAsync(new AgentFilePath(path), CancellationToken.None);

    private static EnvironmentVariableDefinition CreateEnvironmentVariable(string name, string value, Guid id)
        => new EnvironmentVariableDefinition.Builder
        {
            SchemaName = new EnvironmentVariableDefinitionSchemaName($"{AgentSchema}.{name}"),
            DisplayName = name,
            Id = new EnvironmentVariableDefinitionId(id),
            DefaultValue = value,
            ValueComponent = new EnvironmentVariableValue.Builder { Id = new EnvironmentVariableValueId(id), DefinitionId = new EnvironmentVariableDefinitionId(id), Value = value },
        }.Build();

    private static ConnectionReference CreateConnectionReference(string logicalName, string connector)
        => new ConnectionReference.Builder
        {
            Id = new ConnectionReferenceId(Guid.NewGuid()),
            ConnectionReferenceLogicalName = new ConnectionReferenceLogicalName(logicalName),
            ConnectorId = new ConnectorId($"/providers/Microsoft.PowerApps/apis/{connector}"),
        }.Build();

    private static string GetConnectionReferencePath(string logicalName) => $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{logicalName}{CliAgentConnectionsWriter.FileExtension}";
    private static DialogComponent CreateTopic(string name, string triggerId, Guid id)
        => new(
            schemaName: $"{AgentSchema}.topic.{name}",
            displayName: name,
            description: string.Empty,
            id: id,
            parentBotComponentId: default,
            dialog: (DialogBase)CodeSerializer.Deserialize<BotElement>($"kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: {triggerId}\n")!);

    private static DialogComponent CreateChildAgent(string schemaSuffix, string displayName)
        => new(
            schemaName: $"{AgentSchema}.agent.{schemaSuffix}",
            displayName: displayName,
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());

    private static async Task WriteChildAgentLinkAsync(IFileAccessor fileAccessor, DialogComponent agent, bool legacyLink)
    {
        var folder = $"agents/{agent.DisplayName}";
        var yaml = legacyLink
            ? "kind: AgentDialog\n"
            : $"mcs.metadata:\n  schemaName: {agent.SchemaNameString}\nkind: AgentDialog\n";
        await fileAccessor.WriteAsync(new AgentFilePath($"{folder}/agent.mcs.yml"), yaml, CancellationToken.None);
        if (legacyLink)
        {
            await fileAccessor.WriteAsync(
                new AgentFilePath($"{folder}/.agent.json"),
                $"{{ \"schemaName\": \"{agent.SchemaNameString}\", \"folderName\": \"{agent.DisplayName}\" }}",
                CancellationToken.None);
        }
    }
}
