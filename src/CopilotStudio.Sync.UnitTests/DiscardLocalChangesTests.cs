// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Text.Json;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class DiscardLocalChangesTests
{
    [Fact]
    public async Task ComponentCollectionCreate_RemovesOnlyCreatedReference()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/discard-references/");
        var accessor = factory.Create(workspace);
        const string existingSchema = "bot_componentcollection_existing";
        const string createdSchema = "bot_componentcollection_created";

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(
            new AgentFilePath("references.mcs.yml"),
            $"componentCollections:\n  - schemaName: {existingSchema}\n  - schemaName: {createdSchema}\n",
            CancellationToken.None);

        var result = synchronizer.DiscardLocalChanges(workspace,
        [
            new Change
            {
                ChangeType = ChangeType.Create,
                ChangeKind = nameof(BotComponentCollection),
                SchemaName = createdSchema,
                Uri = "references.mcs.yml",
            }
        ]);

        var references = ReadText(accessor, "references.mcs.yml");
        Assert.Contains(existingSchema, references);
        Assert.DoesNotContain(createdSchema, references);
        Assert.Equal(1, result.Deleted);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task ComponentCollectionDelete_WithoutBaseline_SkipsRestoreInsteadOfDegrading()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/restore-reference/");
        var accessor = factory.Create(workspace);
        const string existingSchema = "bot_componentcollection_existing";
        const string deletedSchema = "bot_componentcollection_deleted";

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(
            new AgentFilePath("references.mcs.yml"),
            $"componentCollections:\n  - schemaName: {existingSchema}\n",
            CancellationToken.None);

        var result = synchronizer.DiscardLocalChanges(workspace,
        [
            new Change
            {
                ChangeType = ChangeType.Delete,
                ChangeKind = nameof(BotComponentCollection),
                SchemaName = deletedSchema,
                Uri = "references.mcs.yml",
            }
        ]);

        // Without a .references-cache.yml baseline the original reference form cannot be
        // recovered, so the deleted reference is skipped rather than silently restored as a
        // (possibly incorrect) schema-only reference. Retained references are left untouched.
        var references = ReadText(accessor, "references.mcs.yml");
        Assert.Contains(existingSchema, references);
        Assert.DoesNotContain(deletedSchema, references);
        Assert.Equal(0, result.Restored);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(deletedSchema, skipped.SchemaName);
        Assert.Equal("references.mcs.yml", skipped.Path);
    }

    [Fact]
    public async Task ComponentCollectionDelete_RestoresLastSyncedDirectoryReference()
    {
        var (synchronizer, factory, islandService) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/restore-directory-reference/Agent/");
        var collectionWorkspace = workspace.ResolveRelativeRef(new RelativeDirectoryPath("../Collection/"));
        var accessor = factory.Create(workspace);
        var collectionAccessor = factory.Create(collectionWorkspace);
        const string collectionSchema = "bot_componentcollection_directory";
        var collection = CodeSerializer.Deserialize<BotComponentCollection>(
            $"schemaName: {collectionSchema}\ndisplayName: Directory Collection\n")!;
        var agentId = Guid.NewGuid();
        var entityBuilder = CodeSerializer.Deserialize<BotEntity>(
            "kind: Bot\nschemaName: directory_reference_agent\n")!.ToBuilder();
        entityBuilder.CdsBotId = agentId;
        var entity = entityBuilder.Build();
        var cloudDefinition = new BotDefinition()
            .WithEntity(entity)
            .WithComponentCollections([collection]);

        WorkspaceSynchronizer.WriteCloudCache(accessor, cloudDefinition);
        await accessor.WriteAsync(
            new AgentFilePath(".mcs/changetoken.txt"),
            "token-1",
            CancellationToken.None);
        await accessor.WriteAsync(
            new AgentFilePath("references.mcs.yml"),
            $"componentCollections:\n  - schemaName: {collectionSchema}\n",
            CancellationToken.None);
        await collectionAccessor.WriteAsync(
            new AgentFilePath("collection.mcs.yml"),
            $"schemaName: {collectionSchema}\ndisplayName: Directory Collection\n",
            CancellationToken.None);
        var referenceTracker = new ReferenceTracker();
        referenceTracker.MarkDeclaration(collection.SchemaName, collectionWorkspace);
        await synchronizer.ApplyTouchupsAsync(workspace, referenceTracker, CancellationToken.None);

        accessor.Delete(new AgentFilePath("references.mcs.yml"));
        var localDefinition = new BotDefinition().WithEntity(entity);
        islandService
            .Setup(service => service.GetComponentsAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, entity, "token-2"));
        await synchronizer.PullExistingChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            localDefinition,
            new Mock<ISyncDataverseClient>().Object,
            new AgentSyncInfo { AgentId = agentId },
            CancellationToken.None);

        var (_, changes) = synchronizer.GetLocalChanges(
            localDefinition,
            cloudDefinition,
            accessor,
            "token-1");
        var referenceChange = Assert.Single(
            changes.Where(change =>
                change.ChangeType == ChangeType.Delete
                && change.ChangeKind == nameof(BotComponentCollection)));

        var result = synchronizer.DiscardLocalChanges(workspace, localDefinition, [referenceChange]);

        var references = CodeSerializer.Deserialize<ReferencesSourceFile>(
            ReadText(accessor, "references.mcs.yml"))!;
        var restored = Assert.Single(references.ComponentCollections);
        Assert.Equal("../Collection/", restored.Directory);
        Assert.True(!restored.SchemaName.HasValue || string.IsNullOrEmpty(restored.SchemaName.Value));
        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task SyncWithoutDirectoryRewrite_RecreatesDirectoryReferenceBaseline()
    {
        var (synchronizer, factory, islandService) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/same-schema-reference-baseline/Agent/");
        var accessor = factory.Create(workspace);
        var collectionAccessor = factory.Create(
            workspace.ResolveRelativeRef(new RelativeDirectoryPath("../Collection/")));
        const string collectionSchema = "bot_componentcollection_same_schema";
        var agentId = Guid.NewGuid();
        var entityBuilder = CodeSerializer.Deserialize<BotEntity>(
            "kind: Bot\nschemaName: same_schema_agent\n")!.ToBuilder();
        entityBuilder.CdsBotId = agentId;
        var entity = entityBuilder.Build();
        var collection = CodeSerializer.Deserialize<BotComponentCollection>(
            $"schemaName: {collectionSchema}\ndisplayName: Same Schema Collection\n")!;
        await accessor.WriteAsync(
            new AgentFilePath("references.mcs.yml"),
            "componentCollections:\n  - schemaName:\n    directory: ../Collection/\n",
            CancellationToken.None);
        await collectionAccessor.WriteAsync(
            new AgentFilePath("collection.mcs.yml"),
            $"schemaName: {collectionSchema}\ndisplayName: Same Schema Collection\n",
            CancellationToken.None);
        islandService
            .Setup(service => service.GetComponentsAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(
                botComponentChanges: null,
                connectorDefinitionChanges: null,
                environmentVariableChanges: null,
                connectionReferenceChanges: null,
                aIPluginOperationChanges: null,
                componentCollectionChanges: [new BotComponentCollectionInsert(collection)],
                dataverseTableSearchChanges: null,
                dataverseTableSearchEntityConfigurationChanges: null,
                connectedAgentDefinitionChanges: null,
                bot: entity,
                changeToken: "token-2"));

        await synchronizer.SyncWorkspaceAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            changeToken: null,
            updateWorkspaceDirectory: false,
            dataverseClient: new Mock<ISyncDataverseClient>().Object,
            syncInfo: new AgentSyncInfo { AgentId = agentId },
            cloudFlowMetadata: new CloudFlowMetadata(),
            cancellationToken: CancellationToken.None,
            aiPromptMetadata: default,
            syncCustomConnectors: false,
            syncWorkflowsAndPrompts: false);

        accessor.Delete(new AgentFilePath("references.mcs.yml"));
        var result = synchronizer.DiscardLocalChanges(
            workspace,
            new BotDefinition().WithEntity(entity),
            [
                new Change
                {
                    ChangeType = ChangeType.Delete,
                    ChangeKind = nameof(BotComponentCollection),
                    SchemaName = collectionSchema,
                    Uri = "references.mcs.yml",
                }
            ]);

        var references = CodeSerializer.Deserialize<ReferencesSourceFile>(
            ReadText(accessor, "references.mcs.yml"))!;
        Assert.Equal("../Collection/", Assert.Single(references.ComponentCollections).Directory);
        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task ComponentCollectionDelete_RemovesInvalidReplacementReference()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/invalid-replacement-reference/Agent/");
        var collectionWorkspace = workspace.ResolveRelativeRef(new RelativeDirectoryPath("../Collection/"));
        var accessor = factory.Create(workspace);
        var collectionAccessor = factory.Create(collectionWorkspace);
        const string collectionSchema = "bot_componentcollection_replaced";
        var collection = CodeSerializer.Deserialize<BotComponentCollection>(
            $"schemaName: {collectionSchema}\ndisplayName: Replaced Collection\n")!;
        WorkspaceSynchronizer.WriteCloudCache(
            accessor,
            new BotDefinition().WithComponentCollections([collection]));
        await accessor.WriteAsync(
            new AgentFilePath("references.mcs.yml"),
            $"componentCollections:\n  - schemaName: {collectionSchema}\n",
            CancellationToken.None);
        await collectionAccessor.WriteAsync(
            new AgentFilePath("collection.mcs.yml"),
            $"schemaName: {collectionSchema}\ndisplayName: Replaced Collection\n",
            CancellationToken.None);
        var referenceTracker = new ReferenceTracker();
        referenceTracker.MarkDeclaration(collection.SchemaName, collectionWorkspace);
        await synchronizer.ApplyTouchupsAsync(workspace, referenceTracker, CancellationToken.None);
        await accessor.WriteAsync(
            new AgentFilePath("references.mcs.yml"),
            "componentCollections:\n  - schemaName:\n    directory: ../Missing/\n",
            CancellationToken.None);

        var result = synchronizer.DiscardLocalChanges(
            workspace,
            new BotDefinition(),
            [
                new Change
                {
                    ChangeType = ChangeType.Delete,
                    ChangeKind = nameof(BotComponentCollection),
                    SchemaName = collectionSchema,
                    Uri = "references.mcs.yml",
                }
            ]);

        var references = CodeSerializer.Deserialize<ReferencesSourceFile>(
            ReadText(accessor, "references.mcs.yml"))!;
        var restored = Assert.Single(references.ComponentCollections);
        Assert.Equal("../Collection/", restored.Directory);
        Assert.DoesNotContain("Missing", ReadText(accessor, "references.mcs.yml"));
        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task MalformedComponentCollectionReferences_AreNotOverwritten()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/malformed-references/");
        var accessor = factory.Create(workspace);
        const string malformedReferences = "componentCollections: [";

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(
            new AgentFilePath("references.mcs.yml"),
            malformedReferences,
            CancellationToken.None);

        var result = synchronizer.DiscardLocalChanges(workspace,
        [
            new Change
            {
                ChangeType = ChangeType.Create,
                ChangeKind = nameof(BotComponentCollection),
                SchemaName = "bot_componentcollection_created",
                Uri = "references.mcs.yml",
            }
        ]);

        Assert.Equal(malformedReferences, ReadText(accessor, "references.mcs.yml"));
        Assert.Equal(0, result.Deleted);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public void DeletedWorkflow_RestoresWorkflowAndMetadataFiles()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/discard-workflow/");
        var accessor = factory.Create(workspace);
        var workflowId = Guid.NewGuid();
        const string workflowJson = "{ \"version\": 1 }";
        var metadataYaml = $"workflowId: {workflowId}\nname: Restored Flow\n";
        var extensionData = new RecordDataValue(
            ImmutableDictionary<string, DataValue>.Empty
                .Add("clientdata", DataValue.Create(workflowJson))
                .Add("metadata", DataValue.Create(metadataYaml)));
        var workflow = new CloudFlowDefinition(
            displayName: "Restored Flow",
            isEnabled: true,
            workflowId: workflowId,
            extensionData: extensionData);

        WorkspaceSynchronizer.WriteCloudCache(
            accessor,
            new BotDefinition.Builder { Flows = { workflow } }.Build());

        var result = synchronizer.DiscardLocalChanges(workspace,
        [
            new Change
            {
                ChangeType = ChangeType.Delete,
                ChangeKind = BotElementKind.CloudFlowDefinition.ToString(),
                SchemaName = $"Mcs.Workflow.{workflowId}",
                Uri = workflowId.ToString(),
            }
        ]);

        var workflowFolder = $"workflows/RestoredFlow-{workflowId}";
        using var restoredWorkflow = JsonDocument.Parse(ReadText(accessor, $"{workflowFolder}/workflow.json"));
        Assert.Equal(1, restoredWorkflow.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(metadataYaml, ReadText(accessor, $"{workflowFolder}/metadata.yml"));
        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void DeletedCliConnectionReference_RestoresPerReferenceFile()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/discard-connection/");
        var accessor = factory.Create(workspace);
        const string logicalName = "shared_connection";
        const string connectorId = "/providers/Microsoft.PowerApps/apis/shared_office365";
        var connectionReference = new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = logicalName,
            ConnectorId = connectorId,
        }.Build();

        WorkspaceSynchronizer.WriteCloudCache(
            accessor,
            new BotDefinition().WithConnectionReferences([connectionReference]));

        var result = synchronizer.DiscardLocalChanges(workspace,
        [
            new Change
            {
                ChangeType = ChangeType.Delete,
                ChangeKind = nameof(ConnectionReference),
                SchemaName = logicalName,
                Uri = $"infrastructure/connections/{logicalName}.sync.yaml",
            }
        ]);

        var restored = ReadText(accessor, $"infrastructure/connections/{logicalName}.sync.yaml");
        Assert.Contains(logicalName, restored);
        Assert.Contains(connectorId, restored);
        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void DeletedSettingsFile_RestoresIntoMissingParentPath()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/discard-parent/");
        var accessor = factory.Create(workspace);
        var entity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: restored_agent\n")!;

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition().WithEntity(entity));

        var result = synchronizer.DiscardLocalChanges(workspace,
        [
            new Change
            {
                ChangeType = ChangeType.Delete,
                ChangeKind = entity.Kind.ToString(),
                SchemaName = "entity",
                Uri = "deleted-parent/settings.mcs.yml",
            }
        ]);

        Assert.Contains("restored_agent", ReadText(accessor, "deleted-parent/settings.mcs.yml"));
        Assert.Equal(1, result.Restored);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task UpdatedIcon_IsSkippedWithoutChangingBinaryContent()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/discard-icon/");
        var accessor = factory.Create(workspace);
        var iconPath = new AgentFilePath("icon.png");
        byte[] iconBytes = [0x89, 0x50, 0x4E, 0x47];

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(iconPath, iconBytes, CancellationToken.None);

        var result = synchronizer.DiscardLocalChanges(workspace,
        [
            new Change
            {
                ChangeType = ChangeType.Update,
                ChangeKind = nameof(BotEntity),
                SchemaName = "icon",
                Uri = iconPath.ToString(),
            }
        ]);

        using var stream = accessor.OpenRead(iconPath);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        Assert.Equal(iconBytes, memory.ToArray());
        Assert.Equal(0, result.Restored);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public async Task CreatedKnowledgeAttachment_DeletesMetadataAndPayload()
    {
        var (_, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var contentPath = new AgentFilePath("capabilities/knowledge/files/NewKb.txt");
        await accessor.WriteAsync(contentPath, "new knowledge", CancellationToken.None);

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(
            workspace,
            CancellationToken.None,
            checkKnowledgeFiles: true);
        var (_, changes) = await synchronizer.GetLocalChangesAsync(
            workspace,
            definition,
            CancellationToken.None);
        var attachmentChange = Assert.Single(
            changes.Where(change =>
                change.ChangeType == ChangeType.Create
                && change.ChangeKind == BotElementKind.FileAttachmentComponent.ToString()));

        var result = synchronizer.DiscardLocalChanges(workspace, definition, [attachmentChange]);

        Assert.False(accessor.Exists(contentPath));
        Assert.False(accessor.Exists(new AgentFilePath(attachmentChange.Uri)));
        Assert.Equal(1, result.Deleted);
        Assert.Empty(result.Skipped);
    }

    [Theory]
    [InlineData("capabilities/knowledge/files/NewKb.mcs.yml")]
    [InlineData("capabilities/knowledge/files/NewKb.txt")]
    public async Task CreatedKnowledgeAttachment_DeleteFailure_RestoresMetadataAndPayload(
        string faultDeletePath)
    {
        var workspace = new DirectoryPath("c:/test/discard-knowledge-rollback/");
        var factory = new FaultOnDeleteFileAccessorFactory(faultDeletePath);
        var accessor = factory.Create(workspace);
        var synchronizer = new WorkspaceSynchronizer(
            new SyncMcsFileParser(LspProjectorService.Instance),
            factory,
            new Mock<IIslandControlPlaneService>().Object,
            new TestSyncProgress([]),
            new LspComponentPathResolver());
        var metadataPath = new AgentFilePath("capabilities/knowledge/files/NewKb.mcs.yml");
        var contentPath = new AgentFilePath("capabilities/knowledge/files/NewKb.txt");
        const string schemaName = "new_knowledge_attachment";

        await accessor.WriteAsync(metadataPath, "kind: FileAttachment\n", CancellationToken.None);
        await accessor.WriteAsync(contentPath, "new knowledge", CancellationToken.None);
        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());

        var component = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName("NewKb.txt")
            .WithDescription("desc");
        var definition = new BotDefinition().WithComponents([component]);
        var result = synchronizer.DiscardLocalChanges(
            workspace,
            definition,
            [
                new Change
                {
                    ChangeType = ChangeType.Create,
                    ChangeKind = BotElementKind.FileAttachmentComponent.ToString(),
                    SchemaName = schemaName,
                    Uri = metadataPath.ToString(),
                }
            ]);

        Assert.True(accessor.Exists(metadataPath));
        Assert.True(accessor.Exists(contentPath));
        Assert.Equal(0, result.Deleted);
        Assert.Single(result.Skipped);
    }

    [Fact]
    public async Task FailedRestore_DoesNotPreventLaterCreateDeletion()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/discard-partial-failure/");
        var accessor = factory.Create(workspace);
        var createdPath = new AgentFilePath("topics/created.mcs.yml");

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(createdPath, "kind: AdaptiveDialog\n", CancellationToken.None);

        var result = synchronizer.DiscardLocalChanges(workspace,
        [
            new Change
            {
                ChangeType = ChangeType.Update,
                ChangeKind = BotElementKind.AdaptiveDialog.ToString(),
                SchemaName = "missing.schema",
                Uri = "topics/missing.mcs.yml",
            },
            new Change
            {
                ChangeType = ChangeType.Create,
                ChangeKind = BotElementKind.AdaptiveDialog.ToString(),
                SchemaName = "created.schema",
                Uri = createdPath.ToString(),
            }
        ]);

        Assert.False(accessor.Exists(createdPath));
        Assert.Equal(1, result.Deleted);
        Assert.Single(result.Skipped);
    }

    private static string ReadText(IFileAccessor accessor, string path)
    {
        using var stream = accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class FaultOnDeleteFileAccessorFactory : IFileAccessorFactory
    {
        private readonly string _faultDeletePath;
        private readonly Dictionary<string, IFileAccessor> _accessors = [];

        public FaultOnDeleteFileAccessorFactory(string faultDeletePath)
        {
            _faultDeletePath = faultDeletePath;
        }

        public IFileAccessor Create(DirectoryPath workspaceFolder)
        {
            var key = workspaceFolder.ToString();
            if (!_accessors.TryGetValue(key, out var accessor))
            {
                accessor = new FaultOnDeleteFileAccessor(
                    new InMemoryFileAccessor(workspaceFolder),
                    _faultDeletePath);
                _accessors[key] = accessor;
            }

            return accessor;
        }
    }

    private sealed class FaultOnDeleteFileAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;
        private readonly string _faultDeletePath;

        public FaultOnDeleteFileAccessor(IFileAccessor inner, string faultDeletePath)
        {
            _inner = inner;
            _faultDeletePath = faultDeletePath;
        }

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public void Delete(AgentFilePath path)
        {
            if (string.Equals(path.ToString(), _faultDeletePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Injected IO failure deleting '{path}'.");
            }

            _inner.Delete(path);
        }

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
            => _inner.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(
            string? relativeFolder = null,
            string filePattern = "*.*")
            => _inner.ListFiles(relativeFolder, filePattern);
    }
}
