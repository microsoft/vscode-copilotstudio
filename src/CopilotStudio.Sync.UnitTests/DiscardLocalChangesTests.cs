// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
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
    public async Task ComponentCollectionDelete_RestoresOnlyDeletedReference()
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

        var references = ReadText(accessor, "references.mcs.yml");
        Assert.Contains(existingSchema, references);
        Assert.Contains(deletedSchema, references);
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
}
