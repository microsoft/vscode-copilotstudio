// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class SettingsLocalChangeTests
{
    private const string BotSchemaName = "crf9a_cliagent_S4i9ES";
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");

    private static readonly string ClassicSettings = AgentWorkspaceHostPushTests.Text(ClassicAgentFixtures.AgentD1("Agent D1"), "Agent D1/settings.mcs.yml");

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

        var cachedEntity = (((BotDefinition)ReadCache(accessor)).Entity ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        using var writer = new StringWriter();
        CodeSerializer.SerializeWithoutKind(writer, cachedEntity.WithOnlySettingsYamlProperties());

        Assert.Equal(writer.ToString(), onDisk);
    }

    [Theory]
    [InlineData("trailing ASCII space before a line break", "You are an agent \nSecond line")]
    [InlineData("ASCII space starting a continuation line", "You are an agent\n Second line")]
    [InlineData("whitespace-only interior line", "You are an agent\n   \nThird line")]
    [InlineData("trailing line break", "You are an agent\n")]
    [InlineData("CRLF line break", "You are an agent\r\nSecond line")]
    [InlineData("tab in the middle of a line", "You are an\tagent")]
    [InlineData("trailing non-breaking space", "You are an agent\u00A0")]
    [InlineData("non-breaking space before a line break", "You are an agent \u00A0\nSecond line")]
    [InlineData("zero-width space and non-joiner", "You are an agent\u200B\u200C")]
    [InlineData("zero-width space starting a continuation line", "You are an agent\n\u200BSecond line")]
    public async Task GetLocalChanges_ImmediatelyAfterClone_InvisibleCharactersInInstructions_ReportsNoSettingsChange(
        string scenario,
        string instructionValue)
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(CreateAgentSettingsJson(instructionValue));

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        Assert.True(changes.IsEmpty, BuildRoundTripReport(scenario, instructionValue, accessor, changes));
    }

    [Theory]
    [InlineData("non-breaking space starts a continuation line", "Alpha\n\u00A0Bravo")]
    [InlineData("non-breaking space starts the third line", "Alpha\nBravo\n\u00A0Charlie")]
    [InlineData("non-breaking space is the whole last line", "Alpha\n\u00A0")]
    [InlineData("tab starts a continuation line", "Alpha\n\tBravo")]
    [InlineData("lone CR line break", "You are an agent\rSecond line")]
    [InlineData("customer repro from botdefinition.json", "You are an \u00A0\n \u200B\u200C\u200B\u200C\u200B\u200C\u00A0\u200B\u200C\u200B\u200C\u200B\u200C\u200B\u200C\n\u00A0\u200B\u200C")]
    public async Task GetLocalChanges_ImmediatelyAfterClone_LineBreakFollowedByNonBreakingSpaceOrTab_ReportsNoSettingsChange(
        string scenario,
        string instructionValue)
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(CreateAgentSettingsJson(instructionValue));

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        Assert.True(changes.IsEmpty, BuildRoundTripReport(scenario, instructionValue, accessor, changes));
    }

    [Theory]
    [InlineData("trailing ASCII space before a line break", "You are an agent \nSecond line")]
    [InlineData("trailing non-breaking space", "You are an agent\u00A0")]
    [InlineData("non-breaking space starts a continuation line", "Alpha\n\u00A0Bravo")]
    [InlineData("customer repro from botdefinition.json", "You are an \u00A0\n \u200B\u200C\u200B\u200C\u200B\u200C\u00A0\u200B\u200C\u200B\u200C\u200B\u200C\u200B\u200C\n\u00A0\u200B\u200C")]
    public async Task Clone_InvisibleCharactersInInstructions_SettingsFileMatchesCachedProjection(
        string scenario,
        string instructionValue)
    {
        var (_, accessor, _) = await CloneAsync(CreateAgentSettingsJson(instructionValue));

        var onDisk = ReadFile(accessor, SettingsPath);
        var cachedProjection = SerializeSettingsProjection((((BotDefinition)ReadCache(accessor)).Entity ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")));

        Assert.True(
            string.Equals(cachedProjection, onDisk, StringComparison.Ordinal),
            $"""
            Scenario '{scenario}': settings.mcs.yml does not match the cached projection it was written from.
            Cached projection: {Escape(cachedProjection)}
            On disk          : {Escape(onDisk)}
            """);
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

    [Fact]
    public async Task GetLocalChanges_ImmediatelyAfterCloningAClassicAgent_ReportsNoChange()
    {
        var (synchronizer, _, workspace) = await CloneAsync(ClassicBotEntity());

        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task GetLocalChanges_AfterAClassicSettingsEdit_ReportsOneSettingsChangeWhoseBotKeepsTheCloudsSchemaNameTemplateAndPublishState()
    {
        var cloud = ClassicBotEntity();
        var (synchronizer, accessor, workspace) = await CloneAsync(cloud);
        WriteFile(accessor, SettingsPath, ReadFile(accessor, SettingsPath).Replace("authenticationMode: Integrated", "authenticationMode: None", StringComparison.Ordinal).Replace("contentModeration: Low", "contentModeration: High", StringComparison.Ordinal).Replace("schemaName: crf9a_AgentD1", "schemaName: crf9a_Other", StringComparison.Ordinal).Replace("template: default-2.1.0", "template: empty-1.0.0", StringComparison.Ordinal).TrimEnd() + "\npublishedOn: 2020-01-01T00:00:00.0000000Z\n");

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);

        var change = Assert.Single(changes);
        Assert.Equal(("entity", SettingsPath.ToString(), ChangeType.Update), (change.SchemaName, change.Uri, change.ChangeType));
        Assert.Equal(AuthoredSettingsPushTests.SettingsOf(CodeSerializer.Deserialize<BotEntity>(ClassicSettings.Replace("authenticationMode: Integrated", "authenticationMode: None", StringComparison.Ordinal).Replace("contentModeration: Low", "contentModeration: High", StringComparison.Ordinal))!), AuthoredSettingsPushTests.SettingsOf(changeSet.Bot!));
        Assert.Equal(cloud.PublishedOn, changeSet.Bot!.PublishedOn);
    }

    [Theory]
    [InlineData("schemaName: crf9a_AgentD1", "schemaName: crf9a_Other")]
    [InlineData("template: default-2.1.0", "template: empty-1.0.0")]
    [InlineData("language: 1033", "language: 1033\npublishedOn: 2020-01-01T00:00:00.0000000Z")]
    public async Task GetLocalChanges_WhenOnlyTheClassicSchemaNameTemplateOrPublishStateIsEdited_ReportsNoChange(string original, string edited)
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(ClassicBotEntity());
        WriteFile(accessor, SettingsPath, ReadFile(accessor, SettingsPath).Replace(original, edited, StringComparison.Ordinal));

        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None), CancellationToken.None);

        Assert.Empty(changes);
    }

    [Fact]
    public async Task ReadWorkspaceDefinition_OfAClassicAgent_ReadsItsSettingsFileOnce()
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(ClassicBotEntity());
        accessor.Reads.Clear();

        await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        Assert.Single(accessor.Reads, path => path == SettingsPath.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("displayName: \"unterminated\n")]
    [InlineData("displayName: Local\ndisplayName: Other\n")]
    [InlineData("<<<<<<< ours\ndisplayName: Local\n=======\ndisplayName: Remote\n>>>>>>> theirs\n")]
    public async Task ReadWorkspaceDefinition_WithAnUnreadableClassicSettingsFile_ReportsExactlyOneDiagnosticForIt(string text)
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(ClassicBotEntity());
        WriteFile(accessor, SettingsPath, text);

        Assert.Equal(SettingsPath.ToString(), Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    private static BotEntity ClassicBotEntity()
    {
        var builder = CodeSerializer.Deserialize<BotEntity>(ClassicSettings)!.ToBuilder();
        builder.PublishedOn = new DateTime(2026, 9, 10, 15, 58, 14, DateTimeKind.Utc);

        return builder.Build();
    }

    private static Task<(WorkspaceSynchronizer synchronizer, InMemoryFileAccessor accessor, DirectoryPath workspace)>
        CloneAsync(string agentSettingsJson) => CloneAsync(CreateCliBotEntityFromCloudJson(agentSettingsJson));

    private static async Task<(WorkspaceSynchronizer synchronizer, InMemoryFileAccessor accessor, DirectoryPath workspace)>
        CloneAsync(BotEntity botEntity)
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/settings-phantom-{Guid.NewGuid():N}/");

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

    private static string CreateAgentSettingsJson(string instructionValue)
    {
        var encodedValue = JsonSerializer.Serialize(instructionValue);
        return $$"""
            {
              "$kind": "AgentSettings",
              "model": { "$kind": "ModelConfig", "series": "GPT56Reasoning" },
              "instructions": {
                "$kind": "Instructions",
                "segments": [ { "$kind": "StaticSegment", "value": {{encodedValue}} } ]
              },
              "greetingText": "Hello",
              "web": { "$kind": "WebSettings", "enableWebSearch": false }
            }
            """;
    }

    private static string BuildRoundTripReport(
        string scenario,
        string instructionValue,
        InMemoryFileAccessor accessor,
        ImmutableArray<Change> changes)
    {
        var onDisk = ReadFile(accessor, SettingsPath);
        var blockStart = onDisk.IndexOf("value:", StringComparison.Ordinal);
        var blockEnd = onDisk.IndexOf("greetingText", StringComparison.Ordinal);
        var block = blockStart >= 0 && blockEnd > blockStart ? onDisk[blockStart..blockEnd] : onDisk;

        return $"""
            Scenario '{scenario}': a freshly cloned workspace reported {changes.Length} local change(s) before the user edited anything.
            Changes                 : {string.Join(", ", changes.Select(c => $"{c.SchemaName}:{c.Uri}"))}
            Cloud instruction value : {Escape(instructionValue)}
            Projected block scalar  : {Escape(block)}
            """;
    }

    private static string SerializeSettingsProjection(BotEntity entity)
    {
        using var writer = new StringWriter();
        CodeSerializer.SerializeWithoutKind(writer, entity.WithOnlySettingsYamlProperties());
        return writer.ToString();
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(character switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                ' ' => "\u00B7",
                _ when character < 0x20 || character > 0x7E => $"\\u{(int)character:X4}",
                _ => character.ToString(),
            });
        }

        return builder.ToString();
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
            return (JsonSerializer.Deserialize<BotEntity>(json, ElementSerializer.CreateOptions()) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
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
            return (JsonSerializer.Deserialize<DefinitionBase>(stream, ElementSerializer.CreateOptions()) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value."));
        }
    }
}
