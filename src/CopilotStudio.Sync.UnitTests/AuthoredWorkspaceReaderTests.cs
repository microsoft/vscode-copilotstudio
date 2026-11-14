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
    private const string CollectionSchemaName = "cr123_collection";
    private const string ChildAgentPath = "agents/Balance Agent/agent.mcs.yml";
    private const string NewChildAgentTopicPath = "agents/Balance Agent/topics/New.mcs.yml";
    private const string NewTopicYaml = "mcs.metadata:\n  componentName: New topic\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n";
    private const string LegacyToolPath = "capabilities/tools/legacy_tool.yaml";
    private const string DataverseConnectionReferenceName = "new_sharedcommondataserviceforapps_6480c125";
    private const string SharePointConnectionReferenceName = "Default_draft_rxzs_q.cr.shared_workiqsharepoint.020c62e5181149bc8c90e45269f66dce";

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

        var definition = Assert.IsType<BotDefinition>((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition);

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

        Assert.True((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.TryGetComponentBySchemaName(GreetingSchemaName, out var greeting));
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
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [CreateChildAgent(childAgentId)]);
        await accessor.WriteAsync(new AgentFilePath(NewChildAgentTopicPath), NewTopicYaml, CancellationToken.None);

        Assert.Equal(new BotComponentId(childAgentId), Assert.Single((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.Components, component => component.DisplayName == "New topic").ParentBotComponentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAuthoredWorkspace_DeletedChildAgentFileWithATopicLeftInItsFolder_IsRejected(bool topicIsCached)
    {
        var childAgentId = Guid.NewGuid();
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, topicIsCached ? [CreateChildAgent(childAgentId), EnvironmentVariableTestFixtures.CreateTopic("Existing", EnvironmentVariableTestFixtures.UnreferencedActivity).WithParentBotComponentId(new BotComponentId(childAgentId))] : [CreateChildAgent(childAgentId)]);
        if (!topicIsCached)
        {
            await accessor.WriteAsync(new AgentFilePath(NewChildAgentTopicPath), NewTopicYaml, CancellationToken.None);
        }

        accessor.Delete(new AgentFilePath(ChildAgentPath));

        Assert.Equal(ChildAgentPath, Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_DeletedChildAgentFolder_ExcludesTheChildAgentAndItsComponents()
    {
        var childAgentId = Guid.NewGuid();
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [CreateChildAgent(childAgentId), EnvironmentVariableTestFixtures.CreateTopic("Existing", EnvironmentVariableTestFixtures.UnreferencedActivity).WithParentBotComponentId(new BotComponentId(childAgentId))]);
        accessor.DeleteDirectory(new AgentFilePath("agents/Balance Agent"));

        Assert.DoesNotContain((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.Components, component => component.Id == childAgentId || component.ParentBotComponentId == childAgentId);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_CachedTranslation_KeepsTheTranslationTypeAndLanguageWithTheLocalContent()
    {
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [EnvironmentVariableTestFixtures.CreateTopic("Greeting", EnvironmentVariableTestFixtures.UnreferencedActivity), new TranslationsComponent(displayName: "Greeting (fr-FR)", id: Guid.NewGuid(), language: (Language)1036, schemaName: TranslationSchemaName, content: new AdaptiveDialog())]);
        await accessor.WriteAsync(new AgentFilePath("translations/Greeting.fr-FR.mcs.yml"), "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n  actions:\n    - kind: SendActivity\n      id: sendMessage\n      activity: Bonjour\n", CancellationToken.None);

        Assert.True((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.TryGetComponentBySchemaName(TranslationSchemaName, out var component));
        var translation = Assert.IsType<TranslationsComponent>(component);
        Assert.Equal(1036, (int?)translation.Language);
        Assert.Contains("Bonjour", YamlSerializer.Serialize(Assert.IsType<AdaptiveDialog>(translation.Content)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HRAgent")]
    [InlineData("FoodLogger")]
    public async Task ReadAuthoredWorkspace_WithoutSettings_IsRejected(string fixtureName)
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone(fixtureName);
        accessor.Delete(new AgentFilePath("settings.mcs.yml"));

        Assert.Equal("settings.mcs.yml", Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_NewerLayoutVersion_IsRejected()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName), $"layoutVersion: {AgentClassifier.CurrentLayoutVersion + 1}\n", CancellationToken.None);

        Assert.Contains($"layoutVersion {AgentClassifier.CurrentLayoutVersion + 1}", (await Assert.ThrowsAsync<InvalidOperationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_CliAgentWithLegacyComponentFile_IsRejected()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        await accessor.WriteAsync(new AgentFilePath(LegacyToolPath), "kind: McpTool\nserverUrl: https://example/mcp\n", CancellationToken.None);

        Assert.Equal(LegacyToolPath, Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_CliAgentWithDeletedConnectionReferenceFiles_ExcludesTheirReferences()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        accessor.Delete(GetConnectionReferencePath(DataverseConnectionReferenceName));
        Assert.Equal(SharePointConnectionReferenceName, Assert.Single((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.ConnectionReferences).ConnectionReferenceLogicalName.Value);
        accessor.Delete(GetConnectionReferencePath(SharePointConnectionReferenceName));

        Assert.Empty((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.ConnectionReferences);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_CliAgentWithUnreadableConnectionReferenceFile_IsRejected()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        await accessor.WriteAsync(GetConnectionReferencePath(DataverseConnectionReferenceName), "this is not valid yaml: : :\n   bad indent\n", CancellationToken.None);

        Assert.Equal(GetConnectionReferencePath(DataverseConnectionReferenceName).ToString(), Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_ComponentCollection_ReadsTheConnectionReferencesItsFileDeclares()
    {
        var (synchronizer, accessor, _, workspace) = EnvironmentVariableTestFixtures.CreateWorkspace();
        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotComponentCollectionDefinition().WithComponentCollection(new BotComponentCollection(schemaName: CollectionSchemaName)).WithConnectionReferences([new ConnectionReference(connectionReferenceLogicalName: $"{CollectionSchemaName}.shared_office365.cached", connectionId: string.Empty, connectorId: "/providers/Microsoft.PowerApps/apis/shared_office365")]));
        await accessor.WriteAsync(new AgentFilePath("collection.mcs.yml"), $"schemaName: {CollectionSchemaName}\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("connectionreferences.mcs.yml"), $"connectionReferences:\n  - connectionReferenceLogicalName: {CollectionSchemaName}.shared_office365.declared\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_office365\n", CancellationToken.None);

        Assert.Equal($"{CollectionSchemaName}.shared_office365.declared", Assert.Single((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.ConnectionReferences).ConnectionReferenceLogicalName.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cliagent-1.0.0")]
    public async Task ReadAuthoredWorkspace_DeletedLastEnvironmentVariableFile_ExcludesTheDefinition(string? template)
    {
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, new BotEntity.Builder { SchemaName = new BotEntitySchemaName(EnvironmentVariableTestFixtures.AgentSchemaName), Template = template }.Build(), [EnvironmentVariableTestFixtures.CreateTopic("Greeting", EnvironmentVariableTestFixtures.UnreferencedActivity)], EnvironmentVariableTestFixtures.CreateEnvironmentVariable(EnvironmentVariableTestFixtures.OwnedSchemaName));
        Assert.Equal(EnvironmentVariableTestFixtures.OwnedSchemaName, Assert.Single((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.EnvironmentVariables).SchemaName.Value);
        accessor.Delete(EnvironmentVariableTestFixtures.GetPath(EnvironmentVariableTestFixtures.OwnedSchemaName));

        Assert.Empty((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.EnvironmentVariables);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_ComponentCollectionWithoutEnvironmentVariableFiles_ExcludesTheCachedDefinitions()
    {
        var (synchronizer, accessor, _, workspace) = EnvironmentVariableTestFixtures.CreateWorkspace();
        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotComponentCollectionDefinition().WithComponentCollection(new BotComponentCollection(schemaName: CollectionSchemaName)).WithEnvironmentVariables([EnvironmentVariableTestFixtures.CreateEnvironmentVariable($"{CollectionSchemaName}.owned")]));
        await accessor.WriteAsync(new AgentFilePath("collection.mcs.yml"), $"schemaName: {CollectionSchemaName}\n", CancellationToken.None);

        var authored = await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None);

        Assert.IsType<BotComponentCollectionDefinition>(authored.Definition);
        Assert.Empty(authored.Definition.EnvironmentVariables);
    }

    private static DialogComponent CreateChildAgent(Guid childAgentId) => new(schemaName: "cr123.agent.Agent_2qD", displayName: "Balance Agent", description: string.Empty, id: childAgentId, parentBotComponentId: default, dialog: new AgentDialog());

    private static AgentFilePath GetConnectionReferencePath(string logicalName) => new($"infrastructure/connections/{logicalName}.sync.yaml");
}
