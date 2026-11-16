// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Concurrent;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AuthoredWorkspaceReaderTests
{
    private const string GreetingSchemaName = "cr931_HRAgent.topic.Greeting";
    private const string TranslationSchemaName = "cr123.topic.Greeting.fr-FR";
    private const string CollectionSchemaName = "cr123_collection";
    private const string ChildAgentPath = "agents/Balance Agent/agent.mcs.yml";
    private const string NewChildAgentTopicPath = "agents/Balance Agent/topics/New.mcs.yml";
    private const string UncachedChildAgentPath = "agents/New child/agent.mcs.yml";
    private const string UncachedChildAgentTopicPath = "agents/New child/topics/New.mcs.yml";
    private const string NewTopicYaml = "mcs.metadata:\n  componentName: New topic\nkind: AdaptiveDialog\nbeginDialog:\n  kind: OnRecognizedIntent\n  id: main\n";
    private const string LegacyToolPath = "capabilities/tools/legacy_tool.yaml";
    private const string DataverseConnectionReferenceName = "new_sharedcommondataserviceforapps_6480c125";
    private const string SharePointConnectionReferenceName = "Default_draft_rxzs_q.cr.shared_workiqsharepoint.020c62e5181149bc8c90e45269f66dce";
    private const string ConnectionReferencesPath = "connectionreferences.mcs.yml";
    private const string ComponentCollectionPath = "collection.mcs.yml";
    private const string SecondEnvironmentVariableSchemaName = "cr123.second";

    private static readonly AgentFilePath CachePath = new(".mcs/botdefinition.json");

    [Fact]
    public async Task ReadAuthoredWorkspace_ClassicAgent_ReadsLocalSettingsConnectionReferencesAndWorkflowsWithoutWritingTheCache()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        WorkspaceSynchronizer.WriteCloudCache(accessor, Assert.IsType<BotDefinition>(WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)).WithFlows([new CloudFlowDefinition(displayName: "Removed flow", isEnabled: true, workflowId: new FlowId(Guid.NewGuid()))]));
        var settingsPath = new AgentFilePath("settings.mcs.yml");
        await accessor.WriteAsync(settingsPath, (await accessor.ReadStringAsync(settingsPath, CancellationToken.None)).Replace("displayName: HR Agent", "displayName: Edited HR Agent"), CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath(ConnectionReferencesPath), "connectionReferences:\n  - connectionReferenceLogicalName: cr931_HRAgent.shared_office365.declared\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_office365\n", CancellationToken.None);
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
    public async Task ReadAuthoredWorkspace_KnowledgeFileWhoseDownloadWasSkipped_ListsThePathOfItsContent()
    {
        var (dataverse, _) = EnvironmentVariableTestFixtures.CreateDataverse();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(client => client.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.NotFound, "{\"error\":{\"message\":\"No file attachment found for attribute: filedata\"}}"));
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(dataverse, [KnowledgeFileTestFixtures.CreateFileComponent("cr123.file.Guide.pdf_a1B", "Guide.pdf", Guid.NewGuid())]);

        var knowledgeFile = Assert.Single((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).KnowledgeFiles);

        Assert.Equal("knowledge/files/Guide.pdf", knowledgeFile.RelativePath);
        Assert.False(accessor.Exists(new AgentFilePath(knowledgeFile.RelativePath)));
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_NewTopicInExistingChildAgentFolder_ReferencesTheCloudChildAgent()
    {
        var childAgentId = Guid.NewGuid();
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [CreateChildAgent(childAgentId)]);
        await accessor.WriteAsync(new AgentFilePath(NewChildAgentTopicPath), NewTopicYaml, CancellationToken.None);

        Assert.Equal(new BotComponentId(childAgentId), Assert.Single((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.Components, component => component.DisplayName == "New topic").ParentBotComponentId);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_NewTopicInACaseRenamedChildAgentFolderOnACaseInsensitiveFileSystem_ReferencesTheCloudChildAgent()
    {
        var childAgentId = Guid.NewGuid();
        var (_, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [CreateChildAgent(childAgentId)]);

        Assert.Equal(new BotComponentId(childAgentId), Assert.Single((await CreateSynchronizer(await CopyWithACaseRenamedChildAgentFolderHoldingANewTopicAsync(accessor, new McsCore.InMemoryFileAccessor())).ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.Components, component => component.DisplayName == "New topic").ParentBotComponentId);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_NewTopicInACaseRenamedChildAgentFolderOnACaseSensitiveFileSystem_IsRejectedAtTheCachedChildAgentFile()
    {
        var (_, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [CreateChildAgent(Guid.NewGuid())]);

        Assert.Equal(ChildAgentPath, Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(async () => await CreateSynchronizer(await CopyWithACaseRenamedChildAgentFolderHoldingANewTopicAsync(accessor, new InMemoryFileAccessor(workspace))).ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
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
    public async Task ReadAuthoredWorkspace_ChildAgentFolderWithoutItsAgentFile_IsRejected()
    {
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, [CreateChildAgent(Guid.NewGuid())]);
        await accessor.WriteAsync(new AgentFilePath(UncachedChildAgentTopicPath), NewTopicYaml, CancellationToken.None);

        Assert.Equal(UncachedChildAgentPath, Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_NewChildAgentFolder_ParentsItsTopicToTheNewChildAgent()
    {
        var (synchronizer, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, []);
        await accessor.WriteAsync(new AgentFilePath(UncachedChildAgentPath), "mcs.metadata:\n  componentName: New child\nkind: AgentDialog\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath(UncachedChildAgentTopicPath), NewTopicYaml, CancellationToken.None);

        var components = (await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.Components;

        Assert.Equal(Assert.Single(components, component => component.RootElement is AgentDialog).Id, Assert.Single(components, component => component.DisplayName == "New topic").ParentBotComponentId);
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

    [Theory]
    [InlineData("name: Notify\n")]
    [InlineData("workflowId: 00000000-0000-0000-0000-000000000000\nname: Notify\n")]
    public async Task ReadAuthoredWorkspace_WorkflowMetadataWithoutAWorkflowId_IsRejected(string metadata)
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var workflowFolder = $"workflows/Notify-{Guid.NewGuid()}";
        await accessor.WriteAsync(new AgentFilePath($"{workflowFolder}/workflow.json"), "{\"properties\":{}}", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath($"{workflowFolder}/metadata.yml"), metadata, CancellationToken.None);

        Assert.Equal($"{workflowFolder}/metadata.yml", Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    [Theory]
    [InlineData("workflow.json")]
    [InlineData("metadata.yml")]
    public async Task ReadAuthoredWorkspace_WorkflowFolderMissingOneOfItsFiles_IsRejectedAtTheMissingFile(string missingFile)
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var workflowId = Guid.NewGuid();
        var workflowFolder = $"workflows/Notify-{workflowId}";
        await accessor.WriteAsync(new AgentFilePath($"{workflowFolder}/workflow.json"), "{\"properties\":{}}", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath($"{workflowFolder}/metadata.yml"), $"workflowId: {workflowId}\nname: Notify\n", CancellationToken.None);
        accessor.Delete(new AgentFilePath($"{workflowFolder}/{missingFile}"));

        Assert.Equal($"{workflowFolder}/{missingFile}", Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_WorkflowFolderWithoutEitherWorkflowFile_IsIgnored()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        await accessor.WriteAsync(new AgentFilePath($"workflows/Notify-{Guid.NewGuid()}/notes.md"), "notes", CancellationToken.None);

        Assert.Empty((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.Flows);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_ChecksThatEachWorkflowFileExistsOnce()
    {
        var (_, _, accessor, _, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var workflowId = Guid.NewGuid();
        var workflowFolder = $"workflows/Notify-{workflowId}";
        await accessor.WriteAsync(new AgentFilePath($"{workflowFolder}/workflow.json"), "{\"properties\":{}}", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath($"{workflowFolder}/metadata.yml"), $"workflowId: {workflowId}\nname: Notify\n", CancellationToken.None);
        var countingAccessor = new CountingFileAccessor(accessor);

        Assert.Equal(workflowId, Assert.Single((await CreateSynchronizer(countingAccessor).ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.Flows).WorkflowId.Value);
        Assert.Equal(new[] { 1, 1 }, new[] { "metadata.yml", "workflow.json" }.Select(fileName => countingAccessor.ExistsCount(new AgentFilePath($"{workflowFolder}/{fileName}"))));
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
    public async Task ReadAuthoredWorkspace_CliAgentWithAnUnreadableConnectionReferenceFileNamedLikeAReadableOne_IsRejected()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var unreadablePath = GetConnectionReferencePath(DataverseConnectionReferenceName.ToUpperInvariant());
        await accessor.WriteAsync(unreadablePath, "this is not valid yaml: : :\n   bad indent\n", CancellationToken.None);

        Assert.Equal(unreadablePath.ToString(), Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_CliAgent_ListsAndReadsTheConnectionReferenceFilesOnce()
    {
        var (_, _, accessor, _, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var countingAccessor = new CountingFileAccessor(accessor);

        await CreateSynchronizer(countingAccessor).ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None);

        Assert.Equal(new[] { 1, 1 }, CliAgentConnectionsWriter.Enumerate(accessor).Select(file => countingAccessor.ReadCount(file.Path)));
        Assert.Equal(1, countingAccessor.ListCount(CliAgentConnectionsWriter.InfrastructureConnectionsFolder));
    }

    [Theory]
    [InlineData(null, true, 0)]
    [InlineData("cliagent-1.0.0", true, 1)]
    [InlineData("cliagent-1.0.0", false, 1)]
    public async Task ReadAuthoredWorkspace_ReadsTheManifestsAndListsTheEnvironmentVariablesWorkflowsAndLegacyComponentFoldersOnce(string? template, bool hasCloudCache, int legacyComponentFolderListCount)
    {
        var (_, accessor, _, workspace) = await EnvironmentVariableTestFixtures.CloneAsync(EnvironmentVariableTestFixtures.CreateDataverse().Dataverse, new BotEntity.Builder { SchemaName = new BotEntitySchemaName(EnvironmentVariableTestFixtures.AgentSchemaName), Template = template }.Build(), [EnvironmentVariableTestFixtures.CreateTopic("Greeting", EnvironmentVariableTestFixtures.UnreferencedActivity)], EnvironmentVariableTestFixtures.CreateEnvironmentVariable(EnvironmentVariableTestFixtures.OwnedSchemaName), EnvironmentVariableTestFixtures.CreateEnvironmentVariable(SecondEnvironmentVariableSchemaName));
        await accessor.WriteAsync(new AgentFilePath(ConnectionReferencesPath), "connectionReferences:\n  - connectionReferenceLogicalName: cr123.shared_office365.declared\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_office365\n", CancellationToken.None);
        var workflowId = Guid.NewGuid();
        await accessor.WriteAsync(new AgentFilePath($"workflows/Notify-{workflowId}/workflow.json"), "{\"properties\":{}}", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath($"workflows/Notify-{workflowId}/metadata.yml"), $"workflowId: {workflowId}\nname: Notify\n", CancellationToken.None);
        if (!hasCloudCache)
        {
            accessor.Delete(CachePath);
        }

        var countingAccessor = new CountingFileAccessor(accessor);

        Assert.Equal(workflowId, Assert.Single((await CreateSynchronizer(countingAccessor).ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition.Flows).WorkflowId.Value);
        Assert.Equal(new[] { 1, 1, 1, 1 }, new[] { new AgentFilePath("settings.mcs.yml"), new AgentFilePath(ConnectionReferencesPath), EnvironmentVariableTestFixtures.GetPath(EnvironmentVariableTestFixtures.OwnedSchemaName), EnvironmentVariableTestFixtures.GetPath(SecondEnvironmentVariableSchemaName) }.Select(countingAccessor.ReadCount));
        Assert.Equal(new[] { 1, 1 }, new[] { "environmentvariables", "workflows" }.Select(countingAccessor.ListCount));
        Assert.All(LspProjection.CliComponentBodyFolders, folder => Assert.Equal(legacyComponentFolderListCount, countingAccessor.ListCount(folder, "*.yaml")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadAuthoredWorkspace_ComponentCollection_IsRejectedBeforeListingAnyFolder(bool hasCloudCache)
    {
        var (_, accessor, _, workspace) = EnvironmentVariableTestFixtures.CreateWorkspace();
        await accessor.WriteAsync(new AgentFilePath(ComponentCollectionPath), $"schemaName: {CollectionSchemaName}\n", CancellationToken.None);
        if (hasCloudCache)
        {
            WorkspaceSynchronizer.WriteCloudCache(accessor, new BotComponentCollectionDefinition().WithComponentCollection(new BotComponentCollection(schemaName: CollectionSchemaName)));
        }

        var countingAccessor = new CountingFileAccessor(accessor);

        Assert.Equal(ComponentCollectionPath, Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => CreateSynchronizer(countingAccessor).ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
        Assert.Equal(0, countingAccessor.TotalListCount);
    }

    [Fact]
    public async Task ReadAuthoredWorkspace_CachelessCliAgent_ReadsTheAgentDefinition()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        accessor.Delete(CachePath);

        Assert.IsType<BotDefinition>((await synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None)).Definition);
    }

    [Theory]
    [InlineData("connectionReference:\n  - connectionReferenceLogicalName: cr931_HRAgent.shared_office365.declared\n    connectorId: /providers/Microsoft.PowerApps/apis/shared_office365\n")]
    [InlineData("connectionReferences:\n  - connectorId: /providers/Microsoft.PowerApps/apis/shared_office365\n")]
    [InlineData("connectionReferences: []\n")]
    [InlineData("")]
    public async Task ReadAuthoredWorkspace_ClassicAgentWithAMalformedConnectionReferencesFile_IsRejected(string connectionReferencesYaml)
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        await accessor.WriteAsync(new AgentFilePath(ConnectionReferencesPath), connectionReferencesYaml, CancellationToken.None);

        Assert.Equal(ConnectionReferencesPath, Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadAuthoredWorkspaceAsync(workspace, CancellationToken.None))).Diagnostics).FilePath);
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

    private static DialogComponent CreateChildAgent(Guid childAgentId) => new(schemaName: "cr123.agent.Agent_2qD", displayName: "Balance Agent", description: string.Empty, id: childAgentId, parentBotComponentId: default, dialog: new AgentDialog());

    private static WorkspaceSynchronizer CreateSynchronizer(IFileAccessor fileAccessor) => new(
        new SyncMcsFileParser(LspProjectorService.Instance),
        Mock.Of<IFileAccessorFactory>(factory => factory.Create(It.IsAny<DirectoryPath>()) == fileAccessor),
        Mock.Of<IIslandControlPlaneService>(),
        new TestSyncProgress([]),
        new LspComponentPathResolver());

    private static AgentFilePath GetConnectionReferencePath(string logicalName) => new($"infrastructure/connections/{logicalName}.sync.yaml");

    private static async Task<IFileAccessor> CopyWithACaseRenamedChildAgentFolderHoldingANewTopicAsync(InMemoryFileAccessor fileAccessor, IFileAccessor copy)
    {
        foreach (var file in fileAccessor.Files)
        {
            await copy.WriteAsync(new AgentFilePath(file.Key.Replace("agents/Balance Agent/", "agents/BALANCE AGENT/")), file.Value, CancellationToken.None);
        }

        await copy.WriteAsync(new AgentFilePath("agents/BALANCE AGENT/topics/New.mcs.yml"), NewTopicYaml, CancellationToken.None);
        return copy;
    }

    private sealed class CountingFileAccessor(IFileAccessor fileAccessor) : IFileAccessor
    {
        private readonly ConcurrentDictionary<string, int> _readCounts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _existsCounts = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<(string Folder, string Pattern), int> _listCounts = new();

        public int ReadCount(AgentFilePath path) => _readCounts.TryGetValue(path.ToString(), out var count) ? count : 0;

        public int ExistsCount(AgentFilePath path) => _existsCounts.TryGetValue(path.ToString(), out var count) ? count : 0;

        public int ListCount(string relativeFolder) => _listCounts.Where(entry => entry.Key.Folder == relativeFolder).Sum(entry => entry.Value);

        public int ListCount(string relativeFolder, string filePattern) => _listCounts.TryGetValue((relativeFolder, filePattern), out var count) ? count : 0;

        public int TotalListCount => _listCounts.Values.Sum();

        public bool Exists(AgentFilePath path)
        {
            _existsCounts.AddOrUpdate(path.ToString(), 1, (_, count) => count + 1);
            return fileAccessor.Exists(path);
        }

        public Stream OpenRead(AgentFilePath path)
        {
            _readCounts.AddOrUpdate(path.ToString(), 1, (_, count) => count + 1);
            return fileAccessor.OpenRead(path);
        }

        public Stream OpenWrite(AgentFilePath path) => fileAccessor.OpenWrite(path);

        public void Delete(AgentFilePath path) => fileAccessor.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => fileAccessor.DeleteDirectory(path);

        public void CreateHiddenDirectory(AgentFilePath path) => fileAccessor.CreateHiddenDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => fileAccessor.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
        {
            _listCounts.AddOrUpdate((relativeFolder ?? string.Empty, filePattern), 1, (_, count) => count + 1);
            return fileAccessor.ListFiles(relativeFolder, filePattern);
        }
    }
}
