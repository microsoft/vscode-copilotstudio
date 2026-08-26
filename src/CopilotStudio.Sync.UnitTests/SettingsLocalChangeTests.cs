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

public class SettingsLocalChangeTests
{
    private const string BotSchemaName = "crf9a_cliagent_S4i9ES";
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");

    private const string EmptyStringAgentSettingsJson = """
        {
          "$kind": "AgentSettings",
          "model": { "$kind": "ModelConfig", "series": "GPT56Reasoning" },
          "instructions": {
            "$kind": "Instructions",
            "segments": [ { "$kind": "StaticSegment", "value": "" } ]
          },
          "greetingText": "",
          "web": { "$kind": "WebSettings", "enableWebSearch": false }
        }
        """;

    private const string OmittedAgentSettingsJson = """
        {
          "$kind": "AgentSettings",
          "model": { "$kind": "ModelConfig", "series": "GPT56Reasoning" },
          "instructions": {
            "$kind": "Instructions",
            "segments": [ { "$kind": "StaticSegment" } ]
          },
          "web": { "$kind": "WebSettings", "enableWebSearch": false }
        }
        """;

    private const string EmptyGreetingOnlyJson = """
        {
          "$kind": "AgentSettings",
          "model": { "$kind": "ModelConfig", "series": "GPT56Reasoning" },
          "instructions": {
            "$kind": "Instructions",
            "segments": [ { "$kind": "StaticSegment", "value": "Help the user." } ]
          },
          "greetingText": ""
        }
        """;

    private const string EmptySegmentValueOnlyJson = """
        {
          "$kind": "AgentSettings",
          "model": { "$kind": "ModelConfig", "series": "GPT56Reasoning" },
          "instructions": {
            "$kind": "Instructions",
            "segments": [ { "$kind": "StaticSegment", "value": "" } ]
          },
          "greetingText": "Hello!"
        }
        """;

    private const string SegmentDiagnosticsJson = """
        {
          "$kind": "AgentSettings",
          "model": { "$kind": "ModelConfig", "series": "GPT56Reasoning" },
          "instructions": {
            "$kind": "Instructions",
            "segments": [
              {
                "$kind": "StaticSegment",
                "diagnostics": [
                  {
                    "$kind": "PropertyError",
                    "propertyName": "Value",
                    "errorCode": "MissingRequiredProperty",
                    "errorMessage": "Missing required property 'Value'"
                  }
                ]
              }
            ]
          },
          "web": { "$kind": "WebSettings", "enableWebSearch": false }
        }
        """;

    [Theory]
    [InlineData("empty greetingText and empty segment value", EmptyStringAgentSettingsJson)]
    [InlineData("empty greetingText only", EmptyGreetingOnlyJson)]
    [InlineData("empty segment value only", EmptySegmentValueOnlyJson)]
    [InlineData("segment carries validation diagnostics", SegmentDiagnosticsJson)]
    [InlineData("no empty strings (control)", OmittedAgentSettingsJson)]
    public async Task GetLocalChanges_ImmediatelyAfterClone_ReportsNoSettingsChange(string scenario, string agentSettingsJson)
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(agentSettingsJson);

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        Assert.True(
            changes.IsEmpty,
            $"Scenario '{scenario}': a freshly cloned workspace reported {changes.Length} local change(s) " +
            $"({string.Join(", ", changes.Select(c => $"{c.SchemaName}:{c.Uri}"))}) before the user edited anything.");
    }

    [Fact]
    public async Task Clone_EmptyStringProperties_AreOmittedFromProjectedSettingsFile()
    {
        var (_, accessor, _) = await CloneAsync(EmptyStringAgentSettingsJson);

        var settingsYaml = ReadFile(accessor, SettingsPath);

        Assert.DoesNotContain("greetingText", settingsYaml, StringComparison.Ordinal);
        Assert.Contains("kind: StaticSegment", settingsYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("value:", settingsYaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clone_EmptyStringProperties_SettingsFileMatchesCachedProjectionByte4Byte()
    {
        var (_, accessor, _) = await CloneAsync(EmptyStringAgentSettingsJson);

        var onDisk = ReadFile(accessor, SettingsPath);

        var cachedEntity = ((BotDefinition)ReadCache(accessor)).Entity!;
        using var writer = new StringWriter();
        CodeSerializer.SerializeWithoutKind(writer, cachedEntity.WithOnlySettingsYamlProperties());

        Assert.Equal(writer.ToString(), onDisk);
    }

    [Fact]
    public async Task GetLocalChanges_RealEditToSettingsFile_StillReportsChange()
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(EmptyStringAgentSettingsJson);

        var edited = ReadFile(accessor, SettingsPath)
            .Replace("series: GPT56Reasoning", "series: Sonnet46", StringComparison.Ordinal);
        WriteFile(accessor, SettingsPath, edited);

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        var change = Assert.Single(changes);
        Assert.Equal("entity", change.SchemaName);
        Assert.Equal(SettingsPath.ToString(), change.Uri);
        Assert.Equal(ChangeType.Update, change.ChangeType);
    }

    [Fact]
    public async Task GetLocalChanges_EmptyStringPropertyReplacedWithRealValue_ReportsChange()
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(EmptyStringAgentSettingsJson);

        var edited = ReadFile(accessor, SettingsPath)
            .Replace("    web:", "    greetingText: Welcome!\n\n    web:", StringComparison.Ordinal);
        Assert.Contains("greetingText", edited, StringComparison.Ordinal);
        WriteFile(accessor, SettingsPath, edited);

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        var change = Assert.Single(changes);
        Assert.Equal("entity", change.SchemaName);
        Assert.Equal(SettingsPath.ToString(), change.Uri);
    }


    private static async Task<(WorkspaceSynchronizer synchronizer, InMemoryFileAccessor accessor, DirectoryPath workspace)>
        CloneAsync(string agentSettingsJson)
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/settings-phantom-{Guid.NewGuid():N}/");

        var botEntity = CreateCliBotEntityFromCloudJson(agentSettingsJson);
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), botEntity, "token-1"));

        await synchronizer.CloneChangesAsync(
            workspace,
            new ReferenceTracker(),
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            CreateMockDataverse().Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            CancellationToken.None);

        return (synchronizer, (InMemoryFileAccessor)fileAccessorFactory.Create(workspace), workspace);
    }

    private static BotEntity CreateCliBotEntityFromCloudJson(string agentSettingsJson)
    {
        var json = $$"""
            {
              "$kind": "BotEntity",
              "schemaName": "{{BotSchemaName}}",
              "displayName": "CLI Agent",
              "accessControlPolicy": "GroupMembership",
              "authenticationMode": "Integrated",
              "authenticationTrigger": "Always",
              "template": "cliagent-1.0.0",
              "language": 1033,
              "configuration": {
                "$kind": "BotConfiguration",
                "channels": [ { "$kind": "ChannelDefinition", "id": "MsTeams", "channelId": "MsTeams" } ],
                "publishOnCreate": false,
                "publishOnImport": true,
                "isLightweightBot": false,
                "recognizer": { "$kind": "CLICopilotRecognizer" },
                "agentSettings": {{agentSettingsJson}},
                "deferredProvisioning": false
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

    private static DefinitionBase ReadCache(InMemoryFileAccessor fileAccessor)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(".mcs/botdefinition.json"));
        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions())!;
        }
    }
}
