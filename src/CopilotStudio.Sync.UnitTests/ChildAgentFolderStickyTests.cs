// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ChildAgentFolderStickyTests
{
    private const string Bot = "cre98_AgentC1";

    [Fact]
    public async Task Pull_ChildAgentInSchemaNameFolder_StaysAndRoutesDeltaThere()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-schema-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));

        var cachedDefinition = ReadCache(fileAccessor);
        var newKnowledgeFile = CreateFileComponent($"{Bot}.file.Prices", "Prices", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentInsert(newKnowledgeFile) }, botEntity, "token-2");

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cachedDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        var keys = NormalizedKeys(fileAccessor);
        Assert.Contains("agents/Agent_2qD/agent.mcs.yml", keys);
        Assert.Equal(2, keys.Count(k => k.StartsWith("agents/Agent_2qD/knowledge/files/", StringComparison.Ordinal) && k.EndsWith(".mcs.yml", StringComparison.Ordinal)));
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Balance Agent/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_ChildAgentInDisplayNameFolder_StaysInDisplayNameFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-display-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);

        var cachedDefinition = ReadCache(fileAccessor);
        var newKnowledgeFile = CreateFileComponent($"{Bot}.file.Prices", "Prices", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentInsert(newKnowledgeFile) }, botEntity, "token-2");

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cachedDefinition, mockDataverse.Object, syncInfo, CancellationToken.None);

        var keys = NormalizedKeys(fileAccessor);
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Agent_2qD/", StringComparison.Ordinal));
        Assert.Contains("agents/Balance Agent/agent.mcs.yml", keys);
        Assert.Equal(2, keys.Count(k => k.StartsWith("agents/Balance Agent/knowledge/files/", StringComparison.Ordinal) && k.EndsWith(".mcs.yml", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Pull_ChildComponentDeletedInCloud_SchemaNameFolder_DeletedFromActualFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-del-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));
        Assert.Contains(NormalizedKeys(fileAccessor), k => k.StartsWith("agents/Agent_2qD/knowledge/files/", StringComparison.Ordinal));

        var cached = ReadCache(fileAccessor);
        var cachedFile = cached.Components.OfType<FileAttachmentComponent>().Single();
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentDelete(cachedFile.Id, cachedFile.Version) }, botEntity, "token-2");

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cached, mockDataverse.Object, syncInfo, CancellationToken.None);

        var keys = NormalizedKeys(fileAccessor);
        Assert.Contains("agents/Agent_2qD/agent.mcs.yml", keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Agent_2qD/knowledge/files/", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Balance Agent/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_ChildAgentDeletedInCloud_SchemaNameFolder_FolderPruned()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-delagent-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));

        var cached = ReadCache(fileAccessor);
        var cachedAgent = cached.Components.OfType<DialogComponent>().Single(c => c.RootElement is AgentDialog);
        var cachedFile = cached.Components.OfType<FileAttachmentComponent>().Single();
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentDelete(cachedAgent.Id, cachedAgent.Version),
            new BotComponentDelete(cachedFile.Id, cachedFile.Version),
        }, botEntity, "token-2");

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cached, mockDataverse.Object, syncInfo, CancellationToken.None);

        var keys = NormalizedKeys(fileAccessor);
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Agent_2qD/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reattach_ChildAgentInSchemaNameFolder_WritesToActualFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-reattach-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));

        await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, true, mockDataverse.Object, syncInfo, null, CancellationToken.None);

        var keys = NormalizedKeys(fileAccessor);
        Assert.Contains("agents/Agent_2qD/agent.mcs.yml", keys);
        Assert.Contains(keys, k => k.StartsWith("agents/Agent_2qD/knowledge/files/", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Balance Agent/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Push_ChildAgentInSchemaNameFolder_ConfirmationWriteLandsInActualFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-push-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));

        var cached = ReadCache(fileAccessor);
        var newTopic = CreateChildTopicComponent($"{Bot}.topic.Help", "Help", new BotComponentId(childAgentId));
        var localDefinition = cached.WithComponents(cached.Components.Concat(new BotComponentBase[] { newTopic }));

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuthoringOperationContextBase _, PvaComponentChangeSet cs, CancellationToken _) => cs.WithBot(cs.Bot ?? botEntity));

        await synchronizer.PushLocalChangesAsync(workspace, opContext, localDefinition, mockDataverse.Object, syncInfo, null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        var keys = NormalizedKeys(fileAccessor);
        Assert.Contains(keys, k => k.StartsWith("agents/Agent_2qD/topics/", StringComparison.Ordinal) && k.EndsWith(".mcs.yml", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Balance Agent/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetLocalChanges_ChildComponentInSchemaNameFolder_ChangeUriUsesSchemaNameFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-uri-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));

        var cached = ReadCache(fileAccessor);
        var newTopic = CreateChildTopicComponent($"{Bot}.topic.Help", "Help", new BotComponentId(childAgentId));
        var localDefinition = cached.WithComponents(cached.Components.Concat(new BotComponentBase[] { newTopic }));

        var (_, changes) = synchronizer.GetLocalChanges(localDefinition, cached, fileAccessor, "token-2");

        var change = Assert.Single(changes, c => c.SchemaName == $"{Bot}.topic.Help");
        var uri = change.Uri.Replace('\\', '/');
        Assert.StartsWith("agents/Agent_2qD/", uri);
        Assert.DoesNotContain("Balance Agent", uri);
    }

    [Fact]
    public async Task Pull_ChildAgentRenamedInCloud_SchemaNameFolder_StaysInSchemaNameFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-rename-schema-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));

        var cached = ReadCache(fileAccessor);
        var cachedAgent = cached.Components.OfType<DialogComponent>().Single(c => c.RootElement is AgentDialog);
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentUpdate(cachedAgent.WithDisplayName("Renamed Balance")) }, botEntity, "token-2");

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cached, mockDataverse.Object, syncInfo, CancellationToken.None);

        var keys = NormalizedKeys(fileAccessor);
        Assert.Contains("agents/Agent_2qD/agent.mcs.yml", keys);
        Assert.Contains(keys, k => k.StartsWith("agents/Agent_2qD/knowledge/files/", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Renamed Balance/", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Balance Agent/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_ChildAgentRenamedInCloud_DisplayNameFolder_StaysInOriginalFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-rename-display-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        Assert.Contains("agents/Balance Agent/.agent.json", NormalizedKeys(fileAccessor));

        var cached = ReadCache(fileAccessor);
        var cachedAgent = cached.Components.OfType<DialogComponent>().Single(c => c.RootElement is AgentDialog);
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentUpdate(cachedAgent.WithDisplayName("Renamed Balance")) }, botEntity, "token-2");

        await synchronizer.PullExistingChangesAsync(workspace, opContext, cached, mockDataverse.Object, syncInfo, CancellationToken.None);

        var keys = NormalizedKeys(fileAccessor);
        Assert.Contains("agents/Balance Agent/agent.mcs.yml", keys);
        Assert.Contains("agents/Balance Agent/.agent.json", keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("agents/Renamed Balance/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pull_ChildAgentFolderRenamedWithStaleLink_IsRejected()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-stalelink-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        SetupIslandChangeset(mockIsland, new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Renamed");

        var cachedDefinition = ReadCache(fileAccessor);
        SetupIslandChangeset(mockIsland, Array.Empty<BotComponentChange>(), botEntity, "token-2");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            synchronizer.PullExistingChangesAsync(workspace, opContext, cachedDefinition, mockDataverse.Object, syncInfo, CancellationToken.None));

        var keys = NormalizedKeys(fileAccessor);
        Assert.Contains("agents/Renamed/agent.mcs.yml", keys);
        Assert.DoesNotContain("agents/Balance Agent/agent.mcs.yml", keys);
    }

    [Fact]
    public async Task ListKnowledgeFiles_ChildAgentInSchemaNameFolder_ReturnsSchemaNameFolderPath()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-listkf-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));

        var files = await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None);

        var rates = Assert.Single(files);
        Assert.StartsWith("agents/Agent_2qD/knowledge/files/", rates.RelativePath.Replace('\\', '/'));
        Assert.DoesNotContain("Balance Agent", rates.RelativePath);
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_ChildAgentInSchemaNameFolder_DownloadsIntoSchemaNameFolder()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-dlkf-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var capturedFolders = new List<string>();
        mockDataverse
            .Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string folder, BotComponentId id, string name, CancellationToken ct) => capturedFolders.Add(folder))
            .Returns(Task.CompletedTask);

        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));

        capturedFolders.Clear();
        await synchronizer.DownloadKnowledgeFilesAsync(workspace, mockDataverse.Object, schemaNames: null, CancellationToken.None);

        var folder = Assert.Single(capturedFolders);
        Assert.Contains("agents/Agent_2qD/knowledge/files", folder.Replace('\\', '/'));
        Assert.DoesNotContain("Balance Agent", folder);
    }

    [Fact]
    public void MergeComponent_ChildAgentComponent_PreservesCloudParentBotComponentId()
    {
        var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var schema = $"{Bot}.topic.Help";
        var cloudParentId = new BotComponentId(Guid.NewGuid());
        var localParentId = new BotComponentId(Guid.NewGuid());

        var original = CreateChildTopicComponent(schema, "Help", cloudParentId);
        var local = CreateChildTopicComponent(schema, "Help", localParentId);
        var remote = CreateChildTopicComponent(schema, "Help", cloudParentId);

        var merged = synchronizer.MergeComponent(schema, original, local, remote);

        Assert.NotNull(merged);
        Assert.True(merged!.ParentBotComponentId.HasValue);
        Assert.Equal(cloudParentId, merged.ParentBotComponentId!.Value);
    }

    [Fact]
    public void GetLocalChanges_EditedChildComponent_ParentMissingFromLocalDef_PreservesCloudParent()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath($"c:/test/ws-parent-preserve-{Guid.NewGuid():N}/"));
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = new BotComponentId(Guid.NewGuid());
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId.Value);
        var cloudTopic = CreateChildTopicComponent($"{Bot}.topic.Help", "Help", childAgentId);
        var cloud = new BotDefinition.Builder { Entity = botEntity, Components = { cloudAgent, cloudTopic } }.Build();

        var editedTopic = CreateChildTopicComponent($"{Bot}.topic.Help", "Help EDITED", childAgentId);
        var local = new BotDefinition.Builder { Entity = botEntity, Components = { editedTopic } }.Build();

        var (changeSet, _) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        var update = Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentUpdate>(), c => c.Component?.SchemaNameString == $"{Bot}.topic.Help");
        Assert.True(update.Component!.ParentBotComponentId.HasValue);
        Assert.Equal(childAgentId, update.Component.ParentBotComponentId!.Value);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_NewKnowledgeFileInSchemaNameFolder_IsDiscovered()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-newkf-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Agent_2qD");
        fileAccessor.Delete(new AgentFilePath("agents/Agent_2qD/.agent.json"));
        WriteRawKnowledgeFile(fileAccessor, "agents/Agent_2qD/knowledge/files/NewDoc.csv", "col1,col2\n1,2\n");

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var discovered = Assert.Single(definition.Components.OfType<FileAttachmentComponent>(), c => string.Equals(c.DisplayName, "NewDoc.csv", StringComparison.OrdinalIgnoreCase));
        Assert.True(discovered.ParentBotComponentId.HasValue);
        Assert.Equal(new BotComponentId(childAgentId), discovered.ParentBotComponentId!.Value);
    }

    [Fact]
    public async Task UploadKnowledgeFiles_ChildAgentFolderRenamedWithStaleLink_IsRejected()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-upload-stale-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Renamed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            synchronizer.UploadKnowledgeFilesAsync(workspace, mockDataverse.Object, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_ChildAgentFolderRenamedWithStaleLink_IsRejected()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-sticky-download-stale-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Balance Agent", childAgentId);
        var knowledgeFile = CreateFileComponent($"{Bot}.file.Rates", "Rates", new BotComponentId(childAgentId));
        SetupIslandChangeset(mockIsland, new BotComponentChange[]
        {
            new BotComponentInsert(cloudAgent),
            new BotComponentInsert(knowledgeFile),
        }, botEntity, "token-1");

        var mockDataverse = CreateMockDataverse();
        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), opContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        RelocateChildAgentFolder(fileAccessor, "agents/Balance Agent", "agents/Renamed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            synchronizer.DownloadKnowledgeFilesAsync(workspace, mockDataverse.Object, schemaNames: null, CancellationToken.None));
    }

    private static void WriteRawKnowledgeFile(InMemoryFileAccessor fileAccessor, string path, string content)
    {
        using var stream = fileAccessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static void RelocateChildAgentFolder(InMemoryFileAccessor fileAccessor, string fromFolder, string toFolder)
    {
        var prefix = $"{fromFolder}/";
        var files = fileAccessor.ListFiles(fromFolder)
            .Where(p => p.ToString().Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        foreach (var file in files)
        {
            var relative = file.ToString().Replace('\\', '/').Substring(prefix.Length);
            fileAccessor.Replace(file, new AgentFilePath($"{toFolder}/{relative}"));
        }
    }

    private static void SetupIslandChangeset(Mock<IIslandControlPlaneService> mockIsland, BotComponentChange[] changes, BotEntity botEntity, string token)
        => mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(changes, botEntity, token));

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

    private static DialogComponent CreateAgentDialogComponent(string schemaName, string displayName, Guid id)
        => new(
            schemaName: schemaName,
            displayName: displayName,
            description: string.Empty,
            id: id,
            parentBotComponentId: default,
            dialog: new AgentDialog());

    private static DialogComponent CreateChildTopicComponent(string schemaName, string displayName, BotComponentId parentId)
        => new(
            schemaName: schemaName,
            displayName: displayName,
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: parentId,
            dialog: new AdaptiveDialog());

    private static FileAttachmentComponent CreateFileComponent(string schemaName, string displayName, BotComponentId parentId)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription("desc")
            .ToBuilder();
        builder.Id = Guid.NewGuid();
        builder.ParentBotComponentId = parentId;
        return builder.Build();
    }

    private static List<string> NormalizedKeys(InMemoryFileAccessor fileAccessor)
        => fileAccessor.Files.Keys.Select(k => k.Replace('\\', '/')).ToList();

    private static DefinitionBase ReadCache(InMemoryFileAccessor fileAccessor)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(".mcs/botdefinition.json"));
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions())!;
        }
    }
}
