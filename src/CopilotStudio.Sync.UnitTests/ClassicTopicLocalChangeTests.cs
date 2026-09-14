// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Text;
using System.Text.Json;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ClassicTopicLocalChangeTests
{
    private const string BotSchemaName = "cr160_hrAssistant";
    private const string TopicSchemaName = "cr160_hrAssistant.topic.MasterQuestion";

    private const string CustomerDialog =
        "kind: AdaptiveDialog\r\n activity: \"{Topic.answer.text}\"\r\n\r\ninputType: {}\r\noutputType: {}";

    private const string ManagedPropertiesJson =
        "\"managedProperties\": { \"$kind\": \"ManagedProperties\", \"isCustomizable\": false, \"solutionId\": \"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\" },";

    private const string AuditInfoJson =
        "\"auditInfo\": { \"$kind\": \"AuditInfo\", \"createdTimeUtc\": \"2025-12-10T16:04:02Z\", \"modifiedTimeUtc\": \"2025-12-11T17:15:42Z\", \"createdBy\": \"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\", \"modifiedBy\": \"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\" },";

    private const string ShareContextJson = "\"shareContext\": { \"$kind\": \"ContentShareContext\" },";

    private const string InactiveStateJson = "\"state\": \"Inactive\", \"status\": \"Inactive\",";

    private const string PublisherJson = "\"publisherUniqueName\": \"DefaultPublisherorg6ce34330\",";

    private const string CustomerExtras =
        ManagedPropertiesJson + AuditInfoJson + ShareContextJson + InactiveStateJson + PublisherJson;

    [Theory]
    [InlineData("customer component verbatim", CustomerExtras)]
    [InlineData("without managedProperties", AuditInfoJson + ShareContextJson + InactiveStateJson + PublisherJson)]
    [InlineData("without state/status Inactive", ManagedPropertiesJson + AuditInfoJson + ShareContextJson + PublisherJson)]
    [InlineData("without auditInfo", ManagedPropertiesJson + ShareContextJson + InactiveStateJson + PublisherJson)]
    [InlineData("without shareContext", ManagedPropertiesJson + AuditInfoJson + InactiveStateJson + PublisherJson)]
    [InlineData("without publisherUniqueName", ManagedPropertiesJson + AuditInfoJson + ShareContextJson + InactiveStateJson)]
    [InlineData("only state/status Inactive", InactiveStateJson)]
    [InlineData("only managedProperties", ManagedPropertiesJson)]
    [InlineData("no metadata at all (control)", "")]
    public async Task GetLocalChanges_ImmediatelyAfterClone_CustomerTopicMetadata_ReportsNoTopicChange(
        string scenario,
        string extraJson)
        => await AssertNoLocalChangesAsync(scenario, CustomerDialog, extraJson);

    [Theory]
    [InlineData("customer repro (CRLF, one-space continuation, blank line)", CustomerDialog)]
    [InlineData("same dialog with LF line endings", "kind: AdaptiveDialog\n activity: \"{Topic.answer.text}\"\n\ninputType: {}\noutputType: {}")]
    [InlineData("without the one-space continuation indent", "kind: AdaptiveDialog\r\nactivity: \"{Topic.answer.text}\"\r\n\r\ninputType: {}\r\noutputType: {}")]
    [InlineData("without the blank line", "kind: AdaptiveDialog\r\n activity: \"{Topic.answer.text}\"\r\ninputType: {}\r\noutputType: {}")]
    [InlineData("minimal dialog with CRLF", "kind: AdaptiveDialog\r\ninputType: {}\r\noutputType: {}")]
    [InlineData("minimal dialog with LF", "kind: AdaptiveDialog\ninputType: {}\noutputType: {}")]
    public async Task GetLocalChanges_ImmediatelyAfterClone_ReportsNoTopicChange(string scenario, string dialogYaml)
        => await AssertNoLocalChangesAsync(scenario, dialogYaml, extraJson: "");

    [Theory]
    [InlineData("block scalar line starts with a plain space (control)", " Line two")]
    [InlineData("block scalar line starts with a zero-width space (control)", "\u200BLine two")]
    [InlineData("block scalar line starts with a non-breaking space", "\u00A0Line two")]
    [InlineData("block scalar line starts with a tab", "\tLine two")]
    public async Task GetLocalChanges_ImmediatelyAfterClone_TopicMessageWithInvisibleCharacters_ReportsNoTopicChange(
        string scenario,
        string secondLine)
    {
        var dialog =
            "kind: AdaptiveDialog\n" +
            "beginDialog:\n" +
            "  kind: OnRecognizedIntent\n" +
            "  actions:\n" +
            "    - kind: SendActivity\n" +
            "      activity: |-\n" +
            "        Line one\n" +
            "        " + secondLine + "\n";

        await AssertNoLocalChangesAsync(scenario, dialog, extraJson: "");

        var topic = await ProjectTopicAsync(dialog);
        Assert.True(
            topic.Contains("Line one", StringComparison.Ordinal),
            $"Scenario '{scenario}': the dialog did not project a message block scalar, so this test would be vacuous. Projected topic: {Escape(topic)}");
    }

    private static async Task<string> ProjectTopicAsync(string dialogYaml)
    {
        var (_, accessor, _) = await CloneAsync(dialogYaml, extraJson: "");
        var topicPath = accessor.Files.Keys.Select(k => k.Replace('\\', '/')).FirstOrDefault(k => k.StartsWith("topics/", StringComparison.Ordinal));
        return topicPath is null ? "<topic file not found>" : ReadFile(accessor, topicPath);
    }

    private const string TriggerMissingIntentDialog =
        "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  actions:\n    - kind: SendActivity\n      id: sendOne\n      activity: Hello\n";

    private const string CompleteTriggerDialog =
        "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  intent:\n    triggerQueries:\n      - hello\n  actions:\n    - kind: SendActivity\n      id: sendOne\n      activity: Hello\n";

    [Theory]
    [InlineData("topic missing a required property carries a compile diagnostic", TriggerMissingIntentDialog)]
    [InlineData("topic with a complete trigger", CompleteTriggerDialog)]
    [InlineData("topic with a multi-line message", "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  actions:\n    - kind: SendActivity\n      id: sendOne\n      activity: |-\n        Line one\n        Line two\n")]
    public async Task GetLocalChanges_ImmediatelyAfterClone_TopicWithCompileDiagnostics_ReportsNoTopicChange(
        string scenario,
        string dialogYaml)
        => await AssertNoLocalChangesAsync(scenario, dialogYaml, extraJson: "");

    [Theory]
    [InlineData("message edited on a topic carrying a compile diagnostic", TriggerMissingIntentDialog, "", "activity: Hello", "activity: Goodbye")]
    [InlineData("action id edited on a topic carrying a compile diagnostic", TriggerMissingIntentDialog, "", "id: sendOne", "id: sendTwo")]
    [InlineData("message edited on a topic carrying a compile diagnostic and customer metadata", TriggerMissingIntentDialog, CustomerExtras, "activity: Hello", "activity: Goodbye")]
    [InlineData("message edited on a clean topic carrying customer metadata", CompleteTriggerDialog, CustomerExtras, "activity: Hello", "activity: Goodbye")]
    [InlineData("message edited on a clean topic (control)", CompleteTriggerDialog, "", "activity: Hello", "activity: Goodbye")]
    [InlineData("trigger query edited on a clean topic (control)", CompleteTriggerDialog, "", "- hello", "- goodbye")]
    public async Task GetLocalChanges_AfterRealEditToTopicFile_StillReportsTopicUpdate(
        string scenario,
        string dialogYaml,
        string extraJson,
        string find,
        string replace)
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(dialogYaml, extraJson);

        var topicPath = FindTopicPath(accessor);
        Assert.NotNull(topicPath);

        var original = ReadFile(accessor, topicPath!);
        Assert.Contains(find, original, StringComparison.Ordinal);
        WriteFile(accessor, topicPath!, original.Replace(find, replace, StringComparison.Ordinal));

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        var topicChange = Assert.Single(changes.Where(c => c.SchemaName == TopicSchemaName));

        Assert.True(
            topicChange.ChangeType == ChangeType.Update,
            $"""
            Scenario '{scenario}': a persisted edit to the topic file was not reported as an update.
            Changes        : {string.Join(", ", changes.Select(c => $"{c.ChangeType} {c.SchemaName} -> {c.Uri}"))}
            Edited topic   : {Escape(ReadFile(accessor, topicPath!))}
            """);
    }

    private static string? FindTopicPath(InMemoryFileAccessor accessor)
        => accessor.Files.Keys
            .Select(k => k.Replace('\\', '/'))
            .FirstOrDefault(k => k.StartsWith("topics/", StringComparison.Ordinal));

    private static async Task AssertNoLocalChangesAsync(string scenario, string dialogYaml, string extraJson)
    {
        var (synchronizer, accessor, workspace) = await CloneAsync(dialogYaml, extraJson);

        var localDefinition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, localDefinition, CancellationToken.None);

        var files = string.Join(", ", accessor.Files.Keys.Select(k => k.Replace('\\', '/')).OrderBy(k => k, StringComparer.Ordinal));
        var topicPath = accessor.Files.Keys.Select(k => k.Replace('\\', '/')).FirstOrDefault(k => k.StartsWith("topics/", StringComparison.Ordinal));
        var topicFile = topicPath is null ? "<topic file not found>" : ReadFile(accessor, topicPath);

        Assert.True(
            changes.IsEmpty,
            $"""
            Scenario '{scenario}': a freshly cloned workspace reported {changes.Length} local change(s) before the user edited anything.
            Changes            : {string.Join(", ", changes.Select(c => $"{c.ChangeType} {c.SchemaName} -> {c.Uri}"))}
            Cloud dialog       : {Escape(dialogYaml)}
            Projected topic    : {Escape(topicFile)}
            Workspace files    : {files}
            """);
    }

    private static async Task<(WorkspaceSynchronizer synchronizer, InMemoryFileAccessor accessor, DirectoryPath workspace)>
        CloneAsync(string dialogYaml, string extraJson)
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/classic-topic-{Guid.NewGuid():N}/");

        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {BotSchemaName}")!;
        var components = CreateCloudComponents(dialogYaml, extraJson);

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(
                components.Select(c => (BotComponentChange)new BotComponentInsert(c)).ToArray(),
                botEntity,
                "token-1"));

        await synchronizer.CloneChangesAsync(
            workspace,
            new ReferenceTracker(),
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            CreateMockDataverse().Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            CancellationToken.None);

        return (synchronizer, (InMemoryFileAccessor)fileAccessorFactory.Create(workspace), workspace);
    }

    private static IReadOnlyList<BotComponentBase> CreateCloudComponents(string dialogYaml, string extraJson)
    {
        var json = $$"""
            {
              "$kind": "BotDefinition",
              "components": [
                {
                  "$kind": "GptComponent",
                  "id": "22222222-2222-2222-2222-222222222222",
                  "version": 3565300,
                  "displayName": "HR Assistant",
                  "schemaName": {{JsonSerializer.Serialize(BotSchemaName + ".gpt.default")}},
                  "metadata": "kind: GptComponentMetadata\ninstructions:\n"
                },
                {
                  "$kind": "DialogComponent",
                  "id": "11111111-1111-1111-1111-111111111111",
                  "version": 3565336,
                  "displayName": "Master Question",
                  {{extraJson}}
                  "schemaName": {{JsonSerializer.Serialize(TopicSchemaName)}},
                  "dialog": {{JsonSerializer.Serialize(dialogYaml)}}
                }
              ]
            }
            """;

        using (YamlSerializationContext.UseYamlPassThroughSerializationContext())
        {
            return JsonSerializer.Deserialize<DefinitionBase>(json, ElementSerializer.CreateOptions())!
                .Components.ToList();
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

    private static string ReadFile(InMemoryFileAccessor fileAccessor, string path)
    {
        using var stream = fileAccessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteFile(InMemoryFileAccessor fileAccessor, string path, string content)
    {
        using var stream = fileAccessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream);
        writer.Write(content);
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
}
