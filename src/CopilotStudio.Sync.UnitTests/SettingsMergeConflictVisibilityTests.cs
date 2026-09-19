// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SettingsMergeConflictVisibilityTests
{
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");

    private const string LocalOnlyInstruction = "LOCAL_INSTRUCTION_THAT_MUST_NOT_VANISH";
    private const string RemoteOnlyInstruction = "REMOTE_INSTRUCTION";
    private const string BaseInstruction = "BASE_INSTRUCTION";

    [Fact]
    public void ConflictedMergeOutputStillParsesButLosesBothValues()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var mergedYaml = MergeConflictingInstructions(sync);

        Assert.True(WorkspaceSynchronizer.ContainsConflictMarkers(mergedYaml));

        var reparsed = WorkspaceSynchronizer.TryDeserializeSettingsYaml(mergedYaml);

        Assert.NotNull(reparsed);
        Assert.True(
            string.IsNullOrEmpty(InstructionOf(reparsed!)),
            "This test documents why marker detection is required: a conflicted merge parses cleanly "
            + "while silently dropping the conflicting value, so a null check alone cannot detect it.");
    }

    [Fact]
    public void MergeBotEntitySettings_SameKeyEditedBothSides_ReportsTheConflictedYaml()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var merged = sync.MergeBotEntitySettings(
            MakeEntity(version: 100, instruction: BaseInstruction, series: "Sonnet46"),
            MakeEntity(version: 100, instruction: LocalOnlyInstruction, series: "Sonnet46"),
            MakeEntity(version: 101, instruction: RemoteOnlyInstruction, series: "Sonnet46"),
            out var conflictedSettingsYaml);

        Assert.NotNull(conflictedSettingsYaml);
        Assert.Contains(LocalOnlyInstruction, conflictedSettingsYaml!, StringComparison.Ordinal);
        Assert.Contains(RemoteOnlyInstruction, conflictedSettingsYaml!, StringComparison.Ordinal);
        Assert.True(WorkspaceSynchronizer.ContainsConflictMarkers(conflictedSettingsYaml!));
        Assert.Equal(LocalOnlyInstruction, InstructionOf(merged));
    }

    [Fact]
    public void MergeBotEntitySettings_DisjointEdits_ReportsNoConflict()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var merged = sync.MergeBotEntitySettings(
            MakeEntity(version: 100, instruction: BaseInstruction, series: "Sonnet46"),
            MakeEntity(version: 100, instruction: BaseInstruction, series: "GPT56Reasoning"),
            MakeEntity(version: 101, instruction: RemoteOnlyInstruction, series: "Sonnet46"),
            out var conflictedSettingsYaml);

        Assert.Null(conflictedSettingsYaml);
        Assert.Equal(RemoteOnlyInstruction, InstructionOf(merged));
    }

    [Fact]
    public void ApplyThreeWayMerge_SameKeyEditedBothSides_KeepsTheLocalValueAndSurfacesTheConflict()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var cloud = new BotDefinition().WithEntity(MakeEntity(version: 100, instruction: BaseInstruction, series: "Sonnet46"));
        var local = MakeEntity(version: 100, instruction: LocalOnlyInstruction, series: "Sonnet46");
        var remote = MakeEntity(version: 101, instruction: RemoteOnlyInstruction, series: "Sonnet46");

        var merged = sync.ApplyThreeWayMerge(MakeBotChanges(local), MakeBotChanges(remote), cloud, out var conflictedSettingsYaml);

        Assert.NotNull(conflictedSettingsYaml);
        Assert.NotNull(merged.Bot);
        Assert.Equal(LocalOnlyInstruction, InstructionOf(merged.Bot!));
    }

    [Fact]
    public void ApplyThreeWayMerge_SameKeyEditedBothSides_NeverSilentlyEmptiesTheInstruction()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var cloud = new BotDefinition().WithEntity(MakeEntity(version: 100, instruction: BaseInstruction, series: "Sonnet46"));
        var local = MakeEntity(version: 100, instruction: LocalOnlyInstruction, series: "Sonnet46");
        var remote = MakeEntity(version: 101, instruction: RemoteOnlyInstruction, series: "Sonnet46");

        var merged = sync.ApplyThreeWayMerge(MakeBotChanges(local), MakeBotChanges(remote), cloud, out _);

        Assert.False(
            merged.Bot != null && string.IsNullOrEmpty(InstructionOf(merged.Bot)),
            "The merge produced an agent whose instruction was silently emptied — both the local and "
            + "remote wording were lost.");
    }

    [Fact]
    public async Task Pull_WhenBothSidesEditTheInstruction_WritesConflictMarkersIntoSettingsFile()
    {
        var accessor = await PullWithConflictingInstructionAsync();
        var onDisk = ReadFile(accessor, SettingsPath);

        Assert.True(
            WorkspaceSynchronizer.ContainsConflictMarkers(onDisk),
            $"settings.mcs.yml must keep the conflict markers so the user can choose. Got:\n{onDisk}");
        Assert.Contains(LocalOnlyInstruction, onDisk, StringComparison.Ordinal);
        Assert.Contains(RemoteOnlyInstruction, onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_WhenBothSidesEditTheInstruction_LeavesTheCloudCacheAsThePureRemoteImage()
    {
        var accessor = await PullWithConflictingInstructionAsync();
        var cacheJson = ReadFile(accessor, CachePath);

        Assert.DoesNotContain(LocalOnlyInstruction, cacheJson, StringComparison.Ordinal);
        Assert.DoesNotContain("<<<<<<<", cacheJson, StringComparison.Ordinal);
        Assert.Contains(RemoteOnlyInstruction, cacheJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainsConflictMarkers_IgnoresOrdinarySettingsYaml()
    {
        WorkspaceSynchronizer.TryGetSettingsYaml(
            MakeEntity(version: 100, instruction: BaseInstruction, series: "Sonnet46"),
            out var settingsYaml);

        Assert.False(WorkspaceSynchronizer.ContainsConflictMarkers(settingsYaml!));
        Assert.False(WorkspaceSynchronizer.ContainsConflictMarkers(string.Empty));
    }

    private static async Task<InMemoryFileAccessor> PullWithConflictingInstructionAsync()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/settings-conflict-pull-{Guid.NewGuid():N}/");
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        SetupIsland(mockIsland, MakeEntity(version: 100, instruction: BaseInstruction, series: "Sonnet46"), "token-1");
        await synchronizer.CloneChangesAsync(
            workspace,
            new ReferenceTracker(),
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            CreateMockDataverse().Object,
            syncInfo,
            CancellationToken.None);

        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        WriteFile(accessor, SettingsPath, ReadFile(accessor, SettingsPath).Replace(BaseInstruction, LocalOnlyInstruction, StringComparison.Ordinal));

        SetupIsland(mockIsland, MakeEntity(version: 101, instruction: RemoteOnlyInstruction, series: "Sonnet46"), "token-2");
        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        await synchronizer.PullExistingChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            localDefinition,
            CreateMockDataverse().Object,
            syncInfo,
            CancellationToken.None);

        return accessor;
    }

    private static string MergeConflictingInstructions(WorkspaceSynchronizer sync)
    {
        WorkspaceSynchronizer.TryGetSettingsYaml(MakeEntity(100, BaseInstruction, "Sonnet46"), out var originalYaml);
        WorkspaceSynchronizer.TryGetSettingsYaml(MakeEntity(100, LocalOnlyInstruction, "Sonnet46"), out var localYaml);
        WorkspaceSynchronizer.TryGetSettingsYaml(MakeEntity(101, RemoteOnlyInstruction, "Sonnet46"), out var remoteYaml);
        return sync.MergeStrings(originalYaml, localYaml, remoteYaml);
    }

    private static string? InstructionOf(BotEntity entity)
        => (entity.Configuration?.AgentSettings?.Instructions?.Segments.FirstOrDefault() as StaticSegment)?.Value;

    private static (PvaComponentChangeSet, ImmutableArray<Change>) MakeBotChanges(BotEntity bot)
        => (new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), bot, "token"), ImmutableArray<Change>.Empty);

    private static void SetupIsland(Mock<IIslandControlPlaneService> mockIsland, BotEntity entity, string token)
        => mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), entity, token));

    private static Mock<ISyncDataverseClient> CreateMockDataverse()
    {
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SyncDataverseClient.AIPromptMetadata>());
        return mockDataverse;
    }

    private static string ReadFile(InMemoryFileAccessor fileAccessor, AgentFilePath path)
    {
        using var stream = fileAccessor.OpenRead(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteFile(InMemoryFileAccessor fileAccessor, AgentFilePath path, string content)
    {
        using var stream = fileAccessor.OpenWrite(path);
        using var writer = new StreamWriter(stream);
        writer.Write(content);
    }

    private static BotEntity MakeEntity(int version, string instruction, string series)
    {
        var json = $$"""
        {
          "$kind": "BotDefinition",
          "entity": {
            "$kind": "BotEntity",
            "schemaName": "test_cliagent",
            "displayName": "Test Cli Agent",
            "template": "cliagent-1.0.0",
            "language": 1033,
            "version": {{version}},
            "cdsBotId": "17bf0849-310d-4305-b230-d6fe30b61557",
            "componentIdUnique": "06a66846-79ac-4deb-a842-56085461fcb5",
            "configuration": {
              "$kind": "BotConfiguration",
              "recognizer": { "$kind": "CLICopilotRecognizer" },
              "agentSettings": {
                "$kind": "AgentSettings",
                "instructions": {
                  "$kind": "Instructions",
                  "segments": [
                    { "$kind": "StaticSegment", "value": {{JsonSerializer.Serialize(instruction)}} }
                  ]
                },
                "model": { "$kind": "ModelConfig", "series": {{JsonSerializer.Serialize(series)}} }
              },
              "authoringModel": "CliCopilot"
            }
          }
        }
        """;
        return ReadDefinition(json).Entity!;
    }

    private static BotDefinition ReadDefinition(string botDefinitionJson)
    {
        var accessor = new InMemoryFileAccessor(new DirectoryPath($"c:/test/settings-conflict-{Guid.NewGuid():N}/"));
        var bytes = Encoding.UTF8.GetBytes(botDefinitionJson);
        using (var stream = accessor.OpenWrite(CachePath))
        {
            stream.Write(bytes, 0, bytes.Length);
        }

        return (BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
    }
}
