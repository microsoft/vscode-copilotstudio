// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Text;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class KnowledgeFileUnifyTests
{
    private const string GreetingSchema = "cr931_HRAgent.topic.Greeting";

    private static readonly byte[] FileBytes = Encoding.UTF8.GetBytes("col1,col2\n1,2\n");

    private static readonly BotEntity Agent = new BotEntity.Builder { SchemaName = new BotEntitySchemaName("cr1_agent"), CdsBotId = Guid.NewGuid() }.Build();

    private static AgentSyncInfo MakeSyncInfo() => new AgentSyncInfo
    {
        DataverseEndpoint = new Uri("https://org.crm.dynamics.com/"),
        EnvironmentId = "env",
        AgentId = Guid.NewGuid(),
    };

    [Fact]
    public async Task ListKnowledgeFiles_CliAgent_ReturnsSnapshotFileComponents()
    {
        var (_, definition, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();

        var files = await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None);

        var file = Assert.Single(files);
        Assert.Equal(fileComponent.SchemaNameString, file.SchemaName);
        Assert.Equal(fileComponent.DisplayName, file.FileName);
        Assert.Equal($"capabilities/knowledge/files/{fileComponent.DisplayName}", file.RelativePath);
    }

    [Fact]
    public async Task ListKnowledgeFiles_ClassicAgentWithoutFiles_ReturnsEmpty()
    {
        var (_, _, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");

        var files = await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None);

        Assert.Empty(files);
    }

    [Fact]
    public async Task ListKnowledgeFiles_MatchesDownloadResults()
    {
        var (_, _, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var listed = await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None);
        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, dataverse.Object, schemaNames: null, CancellationToken.None);

        Assert.Equal(
            downloaded.Select(d => d.RelativePath).OrderBy(p => p),
            listed.Select(l => l.RelativePath).OrderBy(p => p));
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_All_DownloadsSnapshotFileComponents()
    {
        var (_, definition, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();

        var downloadedComponentIds = new List<BotComponentId>();
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, BotComponentId, CancellationToken>((_, componentId, _) => downloadedComponentIds.Add(componentId))
            .Returns(Task.CompletedTask);

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, dataverse.Object, schemaNames: null, CancellationToken.None);

        var info = Assert.Single(downloaded);
        Assert.Equal(fileComponent.SchemaNameString, info.SchemaName);
        Assert.Equal($"capabilities/knowledge/files/{fileComponent.DisplayName}", info.RelativePath);
        var downloadedComponentId = Assert.Single(downloadedComponentIds);
        Assert.Equal(fileComponent.Id, downloadedComponentId);
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_All_SkipsMissingFileAttachment()
    {
        var (_, _, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.NotFound, "{\"error\":{\"message\":\"No file attachment found for attribute: filedata\"}}"));

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, dataverse.Object, schemaNames: null, CancellationToken.None);

        Assert.Empty(downloaded);
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_All_KeepsAFileEditedSinceItsLastSyncAndRefreshesTheRest()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-local-edit/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        KnowledgeFileTestFixtures.WriteBotCloudCache(fileAccessor, KnowledgeFileTestFixtures.CreateFileComponent("cr1.file.Edited", "Edited.txt", Guid.NewGuid()), KnowledgeFileTestFixtures.CreateFileComponent("cr1.file.Untouched", "Untouched.txt", Guid.NewGuid()));
        var seeded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, KnowledgeFileTestFixtures.CreateStreamingMock(destination => KnowledgeFileTestFixtures.SeedContent(destination, "cloud-v1")).Object, schemaNames: null, CancellationToken.None);
        var edited = new AgentFilePath(seeded.Single(file => file.FileName == "Edited.txt").RelativePath);
        var untouched = new AgentFilePath(seeded.Single(file => file.FileName == "Untouched.txt").RelativePath);
        await fileAccessor.WriteAsync(edited, "local-edit", CancellationToken.None);

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, KnowledgeFileTestFixtures.CreateStreamingMock(destination => KnowledgeFileTestFixtures.SeedContent(destination, "cloud-v2")).Object, schemaNames: null, CancellationToken.None);

        Assert.Equal("Untouched.txt", Assert.Single(downloaded).FileName);
        Assert.Equal("local-edit", await fileAccessor.ReadStringAsync(edited, CancellationToken.None));
        Assert.Equal("cloud-v2", await fileAccessor.ReadStringAsync(untouched, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_All_ReplacesALocalFileThatWasNeverSynced()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-never-synced/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        KnowledgeFileTestFixtures.WriteBotCloudCache(fileAccessor, KnowledgeFileTestFixtures.CreateFileComponent("cr1.file.Unsynced", "Unsynced.txt", Guid.NewGuid()));
        var unsynced = new AgentFilePath(Assert.Single(await synchronizer.ListKnowledgeFilesAsync(workspace, CancellationToken.None)).RelativePath);
        await fileAccessor.WriteAsync(unsynced, "never-synced", CancellationToken.None);

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, KnowledgeFileTestFixtures.CreateStreamingMock(destination => KnowledgeFileTestFixtures.SeedContent(destination, "cloud-v1")).Object, schemaNames: null, CancellationToken.None);

        Assert.Equal("Unsynced.txt", Assert.Single(downloaded).FileName);
        Assert.Equal("cloud-v1", await fileAccessor.ReadStringAsync(unsynced, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_SelectiveKnownSchema_ThrowsMissingFileAttachment()
    {
        var (_, definition, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.NotFound, "{\"error\":{\"message\":\"No file attachment found for attribute: filedata\"}}"));

        await Assert.ThrowsAsync<DataverseRequestException>(() => synchronizer.DownloadKnowledgeFilesAsync(
            workspace, dataverse.Object, schemaNames: new[] { fileComponent.SchemaNameString }, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_SelectiveUnknownSchema_DownloadsNothing()
    {
        var (_, _, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);
        var streaming = dataverse.As<IStreamingKnowledgeFileClient>();

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace, dataverse.Object, schemaNames: new[] { "cr1d_foodlogger.file.does_not_exist" }, CancellationToken.None);

        Assert.Empty(downloaded);
        streaming.Verify(d => d.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_SelectiveKnownSchema_DownloadsThatFile()
    {
        var (_, definition, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace, dataverse.Object, schemaNames: new[] { fileComponent.SchemaNameString }, CancellationToken.None);

        Assert.Single(downloaded);
        dataverse.As<IStreamingKnowledgeFileClient>().Verify(d => d.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), fileComponent.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UploadKnowledgeFiles_UploadsLocalFilePresentOnDisk()
    {
        var (_, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();
        await accessor.WriteAsync(
            new AgentFilePath($"capabilities/knowledge/files/{fileComponent.DisplayName}"), FileBytes, CancellationToken.None);

        var uploadedFileNames = new List<string>();
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, Guid, string, CancellationToken>((_, _, fileName, _) => uploadedFileNames.Add(fileName))
            .Returns(Task.CompletedTask);

        var uploaded = await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);

        Assert.Contains(fileComponent.DisplayName, uploaded);
        Assert.Contains(fileComponent.DisplayName, uploadedFileNames);
    }

    [Fact]
    public async Task UploadKnowledgeFiles_SkipsFileMissingFromDisk()
    {
        var (_, _, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);
        var streaming = dataverse.As<IStreamingKnowledgeFileClient>();

        var uploaded = await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);

        Assert.Empty(uploaded);
        streaming.Verify(
            d => d.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UploadKnowledgeFiles_UnchangedSinceLastUpload_SkipsSecondUpload()
    {
        var (_, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();
        await accessor.WriteAsync(
            new AgentFilePath($"capabilities/knowledge/files/{fileComponent.DisplayName}"), FileBytes, CancellationToken.None);

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var first = await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);
        var second = await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
        dataverse.As<IStreamingKnowledgeFileClient>().Verify(
            d => d.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UploadKnowledgeFiles_ContentChanged_ReUploads()
    {
        var (_, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();
        var contentPath = new AgentFilePath($"capabilities/knowledge/files/{fileComponent.DisplayName}");
        await accessor.WriteAsync(contentPath, FileBytes, CancellationToken.None);

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);

        await accessor.WriteAsync(contentPath, Encoding.UTF8.GetBytes("col1,col2\n3,4\n9,9\n"), CancellationToken.None);
        var second = await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);

        Assert.Single(second);
        dataverse.As<IStreamingKnowledgeFileClient>().Verify(
            d => d.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task GetLocalChanges_NewKnowledgeFileOnDisk_CreatesComponentWithoutWritingMetadata()
    {
        var (_, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var metadataBefore = accessor.ListFiles("capabilities/knowledge/files", "*.mcs.yml")
            .Select(f => f.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        await accessor.WriteAsync(
            new AgentFilePath("capabilities/knowledge/files/NewKb.txt"), FileBytes, CancellationToken.None);

        var dataverse = new Mock<ISyncDataverseClient>();

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(
            workspace, definition, dataverse.Object, MakeSyncInfo(), CancellationToken.None);

        var insert = Assert.Single(changeSet.BotComponentChanges
                .OfType<BotComponentInsert>()
, i => i.Component is FileAttachmentComponent fac && fac.DisplayName == "NewKb.txt");
        Assert.NotNull(insert);

        var metadataAfter = accessor.ListFiles("capabilities/knowledge/files", "*.mcs.yml")
            .Select(f => f.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(metadataBefore, metadataAfter);
    }

    [Fact]
    public async Task GetLocalChanges_ExistingKnowledgeFileName_DoesNotCreateDuplicateComponent()
    {
        var (_, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();

        await accessor.WriteAsync(
            new AgentFilePath($"capabilities/knowledge/files/{fileComponent.DisplayName}"), FileBytes, CancellationToken.None);

        var dataverse = new Mock<ISyncDataverseClient>();

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(
            workspace, definition, dataverse.Object, MakeSyncInfo(), CancellationToken.None);

        Assert.DoesNotContain(
            changeSet.BotComponentChanges.OfType<BotComponentInsert>(),
            i => i.Component is FileAttachmentComponent);
    }

    [Fact]
    public async Task ReadWorkspace_ClassicWithAnUncachedKnowledgeFileAndItsSidecar_YieldsOneComponent()
    {
        var (_, definition, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var schemaName = await WriteUncachedClassicKnowledgeFileAsync(accessor, definition);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.IsType<FileAttachmentComponent>(Assert.Single(read.Components, component => string.Equals(component.SchemaNameString, schemaName, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task GetLocalChanges_ClassicFirstPushOfAKnowledgeFile_SendsOneInsert()
    {
        var (_, definition, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var schemaName = await WriteUncachedClassicKnowledgeFileAsync(accessor, definition);
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, local, new Mock<ISyncDataverseClient>().Object, MakeSyncInfo(), CancellationToken.None);

        Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentInsert>(), insert => string.Equals(insert.Component?.SchemaNameString, schemaName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReadWorkspace_ANewCliFileDeclaringAnExistingSchemaInAnotherCase_IsNotAddedTwice()
    {
        const string existingSchema = "Default_draft_rxzs_q.tool.ClassifySodiumlevel";
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        await accessor.WriteAsync(new AgentFilePath("capabilities/tools/CaseTwin.mcs.yml"), "mcs.metadata:\n  componentName: Case Twin\n  schemaName: Default_draft_rxzs_q.tool.CLASSIFYSODIUMLEVEL\nkind: ConnectorTool\nauthMode: Invoker\nconnectionReference: cr.shared_teams\nconnectorId: /providers/Microsoft.PowerApps/apis/shared_teams\noperationId: GetSection\n", CancellationToken.None);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        Assert.Equal(existingSchema, Assert.Single(read.Components, component => string.Equals(component.SchemaNameString, existingSchema, StringComparison.OrdinalIgnoreCase)).SchemaNameString);
    }

    [Fact]
    public async Task ReadWorkspace_TwoNewCliFilesDeclaringOneSchemaInAnyCase_AreRefusedNamingBoth()
    {
        var (_, _, _, synchronizer, workspace) = await WriteTwinCliToolsAsync();

        await AssertRefusedNamingBothAsync(synchronizer, workspace, "capabilities/tools/TwinOne.mcs.yml", "capabilities/tools/TwinTwo.mcs.yml");
    }

    [Fact]
    public async Task ReadWorkspace_TwoNewClassicFilesProjectingOneSchema_AreRefusedNamingBoth()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        await accessor.WriteAsync(new AgentFilePath("topics/Duplicate.mcs.yml"), "mcs.metadata:\n  componentName: Duplicate\nkind: AdaptiveDialog\n", CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("topics/copies/Duplicate.mcs.yml"), "mcs.metadata:\n  componentName: Duplicate\nkind: AdaptiveDialog\n", CancellationToken.None);

        await AssertRefusedNamingBothAsync(synchronizer, workspace, "topics/Duplicate.mcs.yml", "topics/copies/Duplicate.mcs.yml");
    }

    [Fact]
    public async Task ReadWorkspace_WithoutValidation_KeepsOneOfTwoNewFilesDeclaringOneSchemaAndReportsTheOther()
    {
        var (_, _, _, synchronizer, workspace) = await WriteTwinCliToolsAsync();
        var diagnostics = new SyncDiagnosticsCollector();

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true, diagnostics, validate: false);

        Assert.Single(read.Components, component => string.Equals(component.SchemaNameString, "Default_draft_rxzs_q.tool.Twin", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith(", so a push would create it twice.", Assert.Single(diagnostics.Diagnostics).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadWorkspace_ASyncedClassicTopicMovedIntoASubfolder_KeepsItWithoutADelete()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        await MoveAsync(accessor, "topics/Greeting.mcs.yml", "topics/archive/Greeting.mcs.yml", await accessor.ReadStringAsync(new AgentFilePath("topics/Greeting.mcs.yml"), CancellationToken.None));

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, read, new Mock<ISyncDataverseClient>().Object, MakeSyncInfo(), CancellationToken.None);

        Assert.Single(read.Components, component => component.SchemaNameString == GreetingSchema);
        Assert.Empty(changeSet.BotComponentChanges.OfType<BotComponentDelete>());
    }

    [Fact]
    public async Task ReadWorkspace_AMovedClassicTopicAndAnotherNewFileWithItsSchema_KeepsOneAndReportsBoth()
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var greeting = await accessor.ReadStringAsync(new AgentFilePath("topics/Greeting.mcs.yml"), CancellationToken.None);
        await MoveAsync(accessor, "topics/Greeting.mcs.yml", "topics/archive/Greeting.mcs.yml", greeting);
        await accessor.WriteAsync(new AgentFilePath("topics/copies/Greeting.mcs.yml"), greeting, CancellationToken.None);

        Assert.Single((await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true, new SyncDiagnosticsCollector(), validate: false)).Components, component => component.SchemaNameString == GreetingSchema);
        await AssertRefusedNamingBothAsync(synchronizer, workspace, "topics/archive/Greeting.mcs.yml", "topics/copies/Greeting.mcs.yml");
    }

    [Fact]
    public async Task ReadWorkspace_AnAdoptedCliToolRenamedInItsFolder_KeepsItWithoutADelete()
    {
        const string classifySodiumLevel = "Default_draft_rxzs_q.tool.ClassifySodiumlevel";
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        await MoveAsync(accessor, "capabilities/tools/ClassifySodiumlevel.mcs.yml", "capabilities/tools/SodiumClassifier.mcs.yml", $"mcs.metadata:\n  componentName: Classify Sodium level\n  schemaName: {classifySodiumLevel}\nkind: WorkflowTool\nworkflowId: 7b9d7530-37d4-575c-ec55-b770b96b1eba\n");

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, read, new Mock<ISyncDataverseClient>().Object, MakeSyncInfo(), CancellationToken.None);

        Assert.Single(read.Components, component => component.SchemaNameString == classifySodiumLevel);
        Assert.Empty(changeSet.BotComponentChanges.OfType<BotComponentDelete>());
    }

    [Theory]
    [InlineData("cr1_agent.topic.GREETING", "is defined by both topics/Greeting.mcs.yml and topics/GREETING.mcs.yml, so a push would create it twice.")]
    [InlineData("cr1_agent.topic.Greeting", "is read twice from topics/Greeting.mcs.yml, so a push would create it twice.")]
    public async Task Push_TwoInsertsWithOneSchemaName_AreRefusedBeforeSending(string secondSchemaName, string reason)
    {
        var (synchronizer, factory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/duplicate-inserts-{Guid.NewGuid():N}/");
        WorkspaceSynchronizer.WriteCloudCache(factory.Create(workspace), new BotDefinition().WithEntity(Agent));

        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.PushLocalChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), Topics("cr1_agent.topic.Greeting", secondSchemaName), new Mock<ISyncDataverseClient>().Object, MakeSyncInfo(), cloudFlowMetadata: null, ImmutableArray<SyncDataverseClient.AIPromptMetadata>.Empty, CancellationToken.None));

        Assert.EndsWith(reason, Assert.Single(failure.Diagnostics).Message, StringComparison.Ordinal);
        island.Verify(service => service.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void GetLocalChanges_OutsideAPush_ListsBothInsertsOfOneSchemaName()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var (changeSet, _) = synchronizer.GetLocalChanges(Topics("cr1_agent.topic.Greeting", "cr1_agent.topic.GREETING"), new BotDefinition().WithEntity(Agent), factory.Create(new DirectoryPath($"c:/test/listed-inserts-{Guid.NewGuid():N}/")), "token-1");

        Assert.Equal(2, changeSet.BotComponentChanges.OfType<BotComponentInsert>().Count());
    }

    [Fact]
    public void GetLocalChanges_RefusingRepeatedInserts_LeavesInsertsWithoutASchemaNameToTheService()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var (changeSet, _) = synchronizer.GetLocalChanges(Topics(string.Empty, string.Empty), new BotDefinition().WithEntity(Agent), factory.Create(new DirectoryPath($"c:/test/unnamed-inserts-{Guid.NewGuid():N}/")), "token-1", isRemoteChange: false, deferMissingParents: false, out _, refuseRepeatedInserts: true);

        Assert.Equal(2, changeSet.BotComponentChanges.OfType<BotComponentInsert>().Count());
    }

    private static BotDefinition Topics(string firstSchemaName, string secondSchemaName) => new BotDefinition().WithEntity(Agent).WithComponents(new[] { Topic(firstSchemaName), Topic(secondSchemaName) });

    private static DialogComponent Topic(string schemaName) => new(schemaName: schemaName, displayName: "Greeting", description: string.Empty, id: Guid.NewGuid(), parentBotComponentId: default, dialog: new AdaptiveDialog());

    private static async Task<(BotEntity entity, BotDefinition definition, InMemoryFileAccessor accessor, WorkspaceSynchronizer synchronizer, DirectoryPath workspace)> WriteTwinCliToolsAsync()
    {
        var clone = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        await clone.accessor.WriteAsync(new AgentFilePath("capabilities/tools/TwinOne.mcs.yml"), TwinTool("Default_draft_rxzs_q.tool.Twin"), CancellationToken.None);
        await clone.accessor.WriteAsync(new AgentFilePath("capabilities/tools/TwinTwo.mcs.yml"), TwinTool("Default_draft_rxzs_q.tool.TWIN"), CancellationToken.None);

        return clone;
    }

    private static string TwinTool(string schemaName) => $"mcs.metadata:\n  componentName: Twin\n  schemaName: {schemaName}\nkind: ConnectorTool\nauthMode: Invoker\nconnectionReference: cr.shared_teams\nconnectorId: /providers/Microsoft.PowerApps/apis/shared_teams\noperationId: GetSection\n";

    private static async Task MoveAsync(InMemoryFileAccessor accessor, string source, string destination, string content)
    {
        await accessor.WriteAsync(new AgentFilePath(destination), content, CancellationToken.None);
        accessor.Delete(new AgentFilePath(source));
    }

    private static async Task AssertRefusedNamingBothAsync(WorkspaceSynchronizer synchronizer, DirectoryPath workspace, string firstFile, string secondFile)
    {
        var message = Assert.Single((await Assert.ThrowsAsync<WorkspaceValidationException>(() => synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true))).Diagnostics).Message;

        Assert.Contains("is defined by both ", message, StringComparison.Ordinal);
        Assert.Contains(firstFile, message, StringComparison.Ordinal);
        Assert.Contains(secondFile, message, StringComparison.Ordinal);
        Assert.EndsWith(", so a push would create it twice.", message, StringComparison.Ordinal);
    }

    private static async Task<string> WriteUncachedClassicKnowledgeFileAsync(InMemoryFileAccessor accessor, BotDefinition definition)
    {
        await accessor.WriteAsync(new AgentFilePath("knowledge/files/abcmain2.txt"), FileBytes, CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("knowledge/files/abcmain2.txt_0Sh.mcs.yml"), "mcs.metadata:\n  componentName: abcmain2.txt\nkind: FileAttachmentComponentMetadata\n", CancellationToken.None);

        return (definition.Entity ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).SchemaName.Value + ".file.abcmain2.txt_0Sh";
    }
}
