// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AuthoredWorkspaceReaderTests
{
    private const string GreetingSchemaName = "cr931_HRAgent.topic.Greeting";
    private const string TranslationSchemaName = "cr123.topic.Greeting.fr-FR";

    private static readonly AgentFilePath CachePath = new(".mcs/botdefinition.json");

    [Fact]
    public async Task ReadAuthoredWorkspace_ClassicAgent_ReadsLocalSettingsConnectionReferencesAndWorkflowsWithoutWritingTheCache()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        WorkspaceSynchronizer.WriteCloudCache(accessor, Assert.IsType<BotDefinition>(WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)).WithFlows([new CloudFlowDefinition(displayName: "Removed flow", isEnabled: true, workflowId: new FlowId(Guid.NewGuid()))]));
        var settingsPath = new AgentFilePath("settings.mcs.yml");
        await accessor.WriteAsync(settingsPath, (await accessor.ReadStringAsync(settingsPath, CancellationToken.None)).Replace("displayName: HR Agent", "displayName: Edited HR Agent"), CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("connectionreferences.mcs.yml"), "connectionReferences:\n  - connectionReferenceLogicalName: cr931_HRAgent.shared_office365.declared\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_office365\n", CancellationToken.None);
        var workflowId = Guid.NewGuid();
        await accessor.WriteAsync(new AgentFilePath($"workflows/Notify-{workflowId}/workflow.json"), "{\"properties\":{}}", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath($"workflows/Notify-{workflowId}/metadata.yml"), $"workflowId: {workflowId}\nname: Notify\n", CancellationToken.None);
        var cachedBytes = await accessor.ReadBytesAsync(CachePath, CancellationToken.None);

        var authored = await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None);

        var definition = Assert.IsType<BotDefinition>(authored.Definition);
        Assert.Equal("Edited HR Agent", definition.Entity?.DisplayName);
        Assert.Equal("cr931_HRAgent.shared_office365.declared", Assert.Single(definition.ConnectionReferences).ConnectionReferenceLogicalName.Value);
        Assert.Equal(workflowId, Assert.Single(definition.Flows).WorkflowId.Value);
        Assert.Equal(cachedBytes, await accessor.ReadBytesAsync(CachePath, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_KeepsCloudOnlyComponentState()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var cache = Assert.IsType<BotDefinition>(WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor));
        WorkspaceSynchronizer.WriteCloudCache(accessor, cache.WithComponents(cache.Components.Select(component => component.SchemaNameString == GreetingSchemaName ? component.WithState(StateCode.Inactive).WithStatus(BotComponentStatusReason.Inactive) : component)));

        var authored = await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None);

        Assert.True(authored.Definition.TryGetComponentBySchemaName(GreetingSchemaName, out var greeting));
        Assert.Equal(StateCode.Inactive, greeting.State?.Value);
        Assert.Equal(BotComponentStatusReason.Inactive, greeting.Status?.Value);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_ListsNewLocalKnowledgeFile()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        await accessor.WriteAsync(new AgentFilePath("knowledge/files/Guide.pdf"), "guide content", CancellationToken.None);

        var authored = await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None);

        var knowledgeFile = Assert.Single(authored.KnowledgeFiles);
        Assert.Equal("knowledge/files/Guide.pdf", knowledgeFile.RelativePath);
        Assert.IsType<FileAttachmentComponent>(Assert.Single(authored.Definition.Components, component => component.SchemaNameString == knowledgeFile.SchemaName));
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_NewTopicInExistingChildAgentFolder_ReferencesTheCloudChildAgent()
    {
        var childAgentId = Guid.NewGuid();
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [new DialogComponent(schemaName: "cr123.agent.Agent_2qD", displayName: "Balance Agent", description: string.Empty, id: childAgentId, parentBotComponentId: default, dialog: new AgentDialog())]);
        await accessor.WriteAsync(new AgentFilePath("agents/Balance Agent/topics/New.mcs.yml"), "mcs.metadata:\n  componentName: New topic\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n", CancellationToken.None);

        var authored = await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None);

        Assert.Equal(new BotComponentId(childAgentId), Assert.Single(authored.Definition.Components, component => component.DisplayName == "New topic").ParentBotComponentId);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_CachedTranslation_KeepsTheTranslationTypeAndLanguageWithTheLocalContent()
    {
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [EnvironmentVariableTestFixtures.CreateTopic("Greeting", EnvironmentVariableTestFixtures.UnreferencedActivity), new TranslationsComponent(displayName: "Greeting (fr-FR)", id: Guid.NewGuid(), language: (Language)1036, schemaName: TranslationSchemaName, content: new AdaptiveDialog())]);
        await accessor.WriteAsync(new AgentFilePath("translations/Greeting.fr-FR.mcs.yml"), "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  actions:\n    - kind: SendActivity\n      id: sendMessage\n      activity: Bonjour\n", CancellationToken.None);

        var authored = await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None);

        Assert.True(authored.Definition.TryGetComponentBySchemaName(TranslationSchemaName, out var component));
        var translation = Assert.IsType<TranslationsComponent>(component);
        Assert.Equal(1036, (int?)translation.Language);
        Assert.Contains("Bonjour", YamlSerializer.Serialize(Assert.IsType<AdaptiveDialog>(translation.Content)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_ClassicAgentWithoutSettings_IsRejected()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        accessor.Delete(new AgentFilePath("settings.mcs.yml"));

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None));

        Assert.Equal("settings.mcs.yml", Assert.Single(failure.Diagnostics).FilePath);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_DeletedLastEnvironmentVariableFile_ExcludesTheDefinition()
    {
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [EnvironmentVariableTestFixtures.CreateTopic("Greeting", EnvironmentVariableTestFixtures.UnreferencedActivity)], EnvironmentVariableTestFixtures.CreateEnvironmentVariable(EnvironmentVariableTestFixtures.OwnedSchemaName));
        Assert.Equal(EnvironmentVariableTestFixtures.OwnedSchemaName, Assert.Single((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.EnvironmentVariables).SchemaName.Value);
        accessor.Delete(EnvironmentVariableTestFixtures.GetPath(EnvironmentVariableTestFixtures.OwnedSchemaName));

        var authored = await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None);

        Assert.Empty(authored.Definition.EnvironmentVariables);
    }
}
