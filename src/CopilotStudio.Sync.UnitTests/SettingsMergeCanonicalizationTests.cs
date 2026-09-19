// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Text.Json;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SettingsMergeCanonicalizationTests
{
    private const string Instruction = "instruction for new tmpl 6";
    private const string PublishedOn = "2026-09-19T03:53:52.0000000Z";
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");

    private const string AuthoredSettings = """
        displayName: "templ6"
        schemaName: "templ6_templ6"
        accessControlPolicy: GroupMembership
        authenticationMode: Integrated
        authenticationTrigger: Always
        configuration:
          authoringModel: CliCopilot
          recognizer:
            kind: CLICopilotRecognizer
          agentSettings:
            model:
              series: Sonnet46
            instructions:
              segments:
                - kind: StaticSegment
                  value: "instruction for new tmpl 6"
        template: cliagent-1.0.0
        language: 1033
        """;

    [Fact]
    public async Task Pull_WhenAuthoredFileMeetsPublishedCloud_KeepsInstruction()
    {
        var (accessor, _) = await InitThenPullAsync(AuthoredSettings);

        Assert.Contains(Instruction, ReadFile(accessor, SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_WhenAuthoredFileMeetsPublishedCloud_WritesTheCanonicalProjection()
    {
        var (accessor, _) = await InitThenPullAsync(AuthoredSettings);

        var cachedEntity = ((BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!).Entity!;
        Assert.Equal(ProjectSettings(cachedEntity), ReadFile(accessor, SettingsPath));
    }

    [Fact]
    public async Task Pull_WhenAuthoredFileMeetsPublishedCloud_BringsDownPublishedOn()
    {
        var (accessor, _) = await InitThenPullAsync(AuthoredSettings);

        Assert.Contains($"publishedOn: {PublishedOn}", ReadFile(accessor, SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_DoesNotWriteAuthoredFormattingIntoTheCloudCache()
    {
        var localOnlyInstruction = "local only instruction that must never reach the cloud cache";
        var edited = AuthoredSettings
            .Replace(Instruction, localOnlyInstruction, StringComparison.Ordinal)
            .Replace("series: Sonnet46", "series: GPT56Reasoning", StringComparison.Ordinal);

        var (accessor, _) = await InitThenPullAsync(edited);

        var onDisk = ReadFile(accessor, SettingsPath);
        Assert.Contains(localOnlyInstruction, onDisk, StringComparison.Ordinal);
        Assert.Contains("series: GPT56Reasoning", onDisk, StringComparison.Ordinal);

        var cacheJson = ReadFile(accessor, CachePath);
        var cachedEntity = ((BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!).Entity!;

        Assert.DoesNotContain(localOnlyInstruction, cacheJson, StringComparison.Ordinal);
        Assert.DoesNotContain("GPT56Reasoning", cacheJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"templ6\\\"", cacheJson, StringComparison.Ordinal);
        Assert.Equal(Instruction, InstructionOf(cachedEntity));
        Assert.Equal("Sonnet46", cachedEntity.Configuration?.AgentSettings?.Model?.Series?.ToString());
        Assert.Equal(PublishedOn, cachedEntity.PublishedOn?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffff'Z'"));
    }

    [Fact]
    public async Task Pull_WhenLocalEditsModelAndCloudPublishes_KeepsBothChanges()
    {
        var edited = AuthoredSettings.Replace("series: Sonnet46", "series: GPT56Reasoning", StringComparison.Ordinal);

        var (accessor, _) = await InitThenPullAsync(edited);

        var onDisk = ReadFile(accessor, SettingsPath);
        Assert.Contains("series: GPT56Reasoning", onDisk, StringComparison.Ordinal);
        Assert.Contains(Instruction, onDisk, StringComparison.Ordinal);
        Assert.Contains($"publishedOn: {PublishedOn}", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pull_WhenLocalEditsInstructionAndCloudPublishes_KeepsLocalInstruction()
    {
        var edited = AuthoredSettings.Replace(Instruction, "locally edited instruction", StringComparison.Ordinal);

        var (accessor, _) = await InitThenPullAsync(edited);

        Assert.Contains("locally edited instruction", ReadFile(accessor, SettingsPath), StringComparison.Ordinal);
    }

    private static async Task<(InMemoryFileAccessor accessor, DirectoryPath workspace)> InitThenPullAsync(string authoredSettings)
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/settings-merge-{Guid.NewGuid():N}/");
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        SetupIsland(mockIsland, CreateEntity(Instruction, publishedOn: null, version: 3635906), "token-1");
        await synchronizer.CloneChangesAsync(
            workspace,
            new ReferenceTracker(),
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            CreateMockDataverse().Object,
            syncInfo,
            CancellationToken.None);

        var accessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        WriteFile(accessor, SettingsPath, authoredSettings);

        SetupIsland(mockIsland, CreateEntity(Instruction, PublishedOn, version: 3635930), "token-2");
        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        await synchronizer.PullExistingChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            localDefinition,
            CreateMockDataverse().Object,
            syncInfo,
            CancellationToken.None);

        return (accessor, workspace);
    }

    private static string? InstructionOf(BotEntity entity)
        => (entity.Configuration?.AgentSettings?.Instructions?.Segments.FirstOrDefault() as StaticSegment)?.Value;

    private static string ProjectSettings(BotEntity entity)
    {
        using var writer = new StringWriter();
        using (YamlSerializationContext.UseStandardSerializationContextIfNotDefined(throwOnInvalidYaml: false))
        {
            YamlSerializer.SerializeWithoutKind(writer, entity.WithOnlySettingsYamlProperties());
        }

        return writer.ToString();
    }

    private static void SetupIsland(Mock<IIslandControlPlaneService> mockIsland, BotEntity entity, string token)
        => mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), entity, token));

    private static BotEntity CreateEntity(string instruction, string? publishedOn, int version)
    {
        var publishedOnLine = publishedOn is null ? string.Empty : $"\"publishedOn\": \"{publishedOn}\",";
        var json = $$"""
            {
              "$kind": "BotEntity",
              "version": {{version}},
              "cdsBotId": "17bf0849-310d-4305-b230-d6fe30b61557",
              "componentIdUnique": "06a66846-79ac-4deb-a842-56085461fcb5",
              "schemaName": "templ6_templ6",
              "displayName": "templ6",
              "accessControlPolicy": "GroupMembership",
              "authenticationMode": "Integrated",
              "authenticationTrigger": "Always",
              {{publishedOnLine}}
              "template": "cliagent-1.0.0",
              "language": 1033,
              "runtimeProvider": "PowerVirtualAgents",
              "state": "Active",
              "status": 1,
              "configuration": {
                "$kind": "BotConfiguration",
                "recognizer": { "$kind": "CLICopilotRecognizer" },
                "agentSettings": {
                  "$kind": "AgentSettings",
                  "model": { "$kind": "ModelConfig", "series": "Sonnet46" },
                  "instructions": {
                    "$kind": "Instructions",
                    "segments": [ { "$kind": "StaticSegment", "value": {{JsonSerializer.Serialize(instruction)}} } ]
                  }
                },
                "authoringModel": "CliCopilot"
              }
            }
            """;

        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<BotEntity>(json, ElementSerializer.CreateOptions())!;
        }
    }

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
}
