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
