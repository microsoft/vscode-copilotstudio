// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.Agents.Platform.Content.Exceptions;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class KnowledgeFileUnifyTests
{
    private const string GreetingSchema = "cr931_HRAgent.topic.Greeting";

    private const string LocalDescription = "Local parking rules.";

    private const string CloudDescription = "Cloud parking rules.";

    private const string LocalContent = "local rules";

    private const string ParkingFileName = "parking.txt";

    private static readonly AgentFilePath ParkingPayloadPath = new($"capabilities/knowledge/files/{ParkingFileName}");

    private static readonly AgentFilePath ParkingSidecarPath = new("capabilities/knowledge/files/parkingtxt_r1.mcs.yml");

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PushChangeset_DeletedKnowledgeFile_KeepsTheLocalFileOnlyWhileAReplacementClaimsItsName(bool replacedUnderTheSameName)
    {
        var infrastructure = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var (entity, definition, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger", infrastructure);
        var deleted = definition.Components.OfType<FileAttachmentComponent>().Single();
        var fileName = deleted.DisplayName ?? throw new Xunit.Sdk.XunitException("Expected a knowledge file name.");
        var contentPath = new AgentFilePath($"capabilities/knowledge/files/{fileName}");
        await accessor.WriteAsync(contentPath, FileBytes, CancellationToken.None);
        var uploads = new List<(Guid ComponentId, string FileName)>();
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.As<IStreamingKnowledgeFileClient>().Setup(d => d.UploadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Stream, Guid, string, CancellationToken>((_, componentId, uploadedFileName, _) => uploads.Add((componentId, uploadedFileName)))
            .Returns(Task.CompletedTask);
        await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);
        var replacement = KnowledgeFileTestFixtures.CreateFileComponent($"{deleted.SchemaNameString}_r4", fileName, Guid.NewGuid());
        var response = new PvaComponentChangeSet(replacedUnderTheSameName ? new BotComponentChange[] { new BotComponentInsert(replacement), new BotComponentDelete(deleted.Id, deleted.Version) } : new BotComponentChange[] { new BotComponentDelete(deleted.Id, deleted.Version) }, entity, "after-replace");
        infrastructure.island.Setup(island => island.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);

        await synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), response, dataverse.Object, Guid.NewGuid(), null, default, CancellationToken.None);
        await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);

        Assert.Equal(replacedUnderTheSameName, accessor.Exists(contentPath));
        Assert.Equal(replacedUnderTheSameName ? new[] { (deleted.Id.Value, fileName), (replacement.Id.Value, fileName) } : new[] { (deleted.Id.Value, fileName) }, uploads);
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
    public async Task GetLocalChanges_EditedKnowledgeFileSidecarDescription_IsSentAsAnUpdate()
    {
        var (synced, _, changeSet) = await GetLocalChangesAfterDescribingTheKnowledgeFileAsync("Nutrient values for every food. Use for questions about calories.");

        var updated = Assert.IsType<FileAttachmentComponent>(Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentUpdate>()).Component);
        Assert.Equal((synced.SchemaNameString, "Nutrient values for every food. Use for questions about calories."), (updated.SchemaNameString, updated.Description));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task GetLocalChanges_KnowledgeFileSidecarWithoutADescription_KeepsTheSyncedDescriptionWithoutAnUpdate(string? authoredDescription)
    {
        var (synced, read, changeSet) = await GetLocalChangesAfterDescribingTheKnowledgeFileAsync(authoredDescription);

        Assert.Empty(changeSet.BotComponentChanges.OfType<BotComponentUpdate>());
        Assert.Equal(synced.Description, Assert.Single(read.Components.OfType<FileAttachmentComponent>()).Description);
    }

    [Fact]
    public async Task Pull_KnowledgeFileReuploadedInTheCloud_KeepsTheLocalSidecarDescriptionPendingAndDownloadsTheUpload()
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(synced => [new BotComponentUpdate(synced.WithVersion(synced.Version + 1))]);

        var pending = Assert.IsType<FileAttachmentComponent>(Assert.Single((await GetLocalChangesAsync(pull.Synchronizer, pull.Workspace)).BotComponentChanges.OfType<BotComponentUpdate>()).Component);
        var cached = ReadCachedKnowledgeFile(pull.Accessor);
        Assert.Equal((LocalDescription, pull.Synced.Version + 1), (pending.Description, pending.Version));
        Assert.Equal((pull.Synced.Description, pull.Synced.Version + 1), (cached.Description, cached.Version));
        Assert.Equal("cloud rules", await pull.Accessor.ReadStringAsync(pull.PayloadPath, CancellationToken.None));
        Assert.Empty(pull.Conflicts);
    }

    [Fact]
    public async Task Pull_KnowledgeFileDescribedDifferentlyInTheCloud_WritesBothDescriptionsBetweenConflictMarkersAndBlocksThePush()
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(DescribeInTheCloud);

        var sidecar = await pull.Accessor.ReadStringAsync(pull.SidecarPath, CancellationToken.None);
        var conflict = Assert.Single(pull.Conflicts);
        var failure = await Assert.ThrowsAsync<WorkspaceValidationException>(() => pull.Synchronizer.ReadWorkspaceDefinitionAsync(pull.Workspace, CancellationToken.None, checkKnowledgeFiles: true));
        Assert.Equal($"mcs.metadata:\n  componentName: {pull.Synced.DisplayName}\n{McsConflictMarkers.OursLine}\n  description: {LocalDescription}\n{McsConflictMarkers.SplitterLine}\n  description: {CloudDescription}\n{McsConflictMarkers.TheirsLine}\n", sidecar);
        Assert.Equal((pull.SidecarPath.ToString(), WorkspaceDiagnosticKind.MergeConflict, 3), (conflict.FilePath, conflict.Kind, conflict.Line));
        Assert.Equal(CloudDescription, ReadCachedKnowledgeFile(pull.Accessor).Description);
        Assert.Equal(pull.SidecarPath.ToString(), Assert.Single(failure.Diagnostics).FilePath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pull_KnowledgeFileDescribedDifferentlyInTheCloud_OnceResolvedPushesTheDescriptionOnlyWhenTheLocalOneIsKept(bool keepLocal)
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(DescribeInTheCloud);
        var discardedLine = $"  description: {(keepLocal ? CloudDescription : LocalDescription)}";
        var sidecar = await pull.Accessor.ReadStringAsync(pull.SidecarPath, CancellationToken.None);
        await pull.Accessor.WriteAsync(pull.SidecarPath, string.Join("\n", sidecar.Split('\n').Where(line => !McsConflictMarkers.IsMarkerLine(line) && line != discardedLine)), CancellationToken.None);

        var pushed = (await GetLocalChangesAsync(pull.Synchronizer, pull.Workspace)).BotComponentChanges.OfType<BotComponentUpdate>().Select(update => (update.Component?.Description, update.Component?.Version));

        Assert.Equal(keepLocal ? new (string?, long?)[] { (LocalDescription, pull.Synced.Version + 1) } : [], pushed);
    }

    [Fact]
    public async Task Pull_KnowledgeFileDeletedInTheCloudAfterALocalDescriptionEdit_KeepsItsFilesAndReportsThemAsKept()
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(DeleteInTheCloud);

        var kept = Assert.Single(pull.Conflicts);
        Assert.Equal(KnowledgeSidecar(pull.Synced.DisplayName, LocalDescription), await pull.Accessor.ReadStringAsync(pull.SidecarPath, CancellationToken.None));
        Assert.Equal(LocalContent, await pull.Accessor.ReadStringAsync(pull.PayloadPath, CancellationToken.None));
        Assert.Empty(ReadCloudCache(pull.Accessor).Components.OfType<FileAttachmentComponent>());
        Assert.Equal((pull.PayloadPath.ToString(), WorkspaceDiagnosticKind.DeletedInEnvironment), (kept.FilePath, kept.Kind));
    }

    [Fact]
    public async Task Pull_KnowledgeFileDeletedInTheCloudAfterALocalDescriptionEdit_RefusesBeforeWritingAnythingWhenAskedTo()
    {
        var pull = await DescribeTheKnowledgeFileAsync(DeleteInTheCloud);
        pull.Dataverse.Setup(client => client.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { new SyncDataverseClient.WorkflowMetadata { WorkflowId = Guid.NewGuid(), Name = "Notify", ClientData = "{}" } });

        await AssertPullStopsBeforeWritingAnythingAsync(pull, DeletedKnowledgeFileEdits.Refuse, pull.PayloadPath, canBeKept: true);
    }

    [Fact]
    public async Task Pull_KnowledgeFileDeletedInTheCloudAfterALocalDescriptionEdit_DeletesItsFilesWhenTheLocalEditIsDiscarded()
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(DeleteInTheCloud, deletedKnowledgeFileEdits: DeletedKnowledgeFileEdits.Discard);

        Assert.False(pull.Accessor.Exists(pull.SidecarPath));
        Assert.False(pull.Accessor.Exists(pull.PayloadPath));
        Assert.Empty(pull.Conflicts);
        Assert.Empty((await GetLocalChangesAsync(pull.Synchronizer, pull.Workspace)).BotComponentChanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pull_KnowledgeFileDeletedInTheCloudAfterALocalDescriptionEdit_IsCreatedAgainWithTheLocalDescriptionOnlyWhileItsFilesAreKept(bool keepFiles)
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(DeleteInTheCloud);
        if (!keepFiles)
        {
            pull.Accessor.Delete(pull.SidecarPath);
            pull.Accessor.Delete(pull.PayloadPath);
        }

        var pushed = (await GetLocalChangesAsync(pull.Synchronizer, pull.Workspace)).BotComponentChanges.Select(change => (change.GetType(), (change as BotComponentUpsert)?.Component?.DisplayName, (change as BotComponentUpsert)?.Component?.Description));

        Assert.Equal(keepFiles ? new (Type, string?, string?)[] { (typeof(BotComponentInsert), pull.Synced.DisplayName, LocalDescription) } : [], pushed);
    }

    [Fact]
    public async Task Push_KnowledgeFileDeletedInTheCloudAfterALocalDescriptionEdit_AsksToGetTheLatestChangesWhichStillOfferToKeepIt()
    {
        var pull = await DescribeTheKnowledgeFileAsync(DeleteInTheCloud);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => pull.PushAsync(overwriteRemoteChanges: false));
        var pullRefusal = await Assert.ThrowsAsync<DeletedKnowledgeFileEditsException>(() => pull.PullAsync(DeletedKnowledgeFileEdits.Refuse));

        Assert.Contains("Get the latest changes", refusal.Message);
        Assert.Single(pull.Pushed);
        Assert.Equal([pull.PayloadPath.ToString()], pullRefusal.FilePaths);
        Assert.True(pullRefusal.CanBeKept);
    }

    [Fact]
    public async Task Push_KnowledgeFileDeletedInTheCloudAfterALocalDescriptionEdit_CreatesItAgainAsANewKnowledgeFileWhenOverwriting()
    {
        var pull = await DescribeTheKnowledgeFileAsync(DeleteInTheCloud);

        await pull.PushAsync(overwriteRemoteChanges: true);

        Assert.Equal(2, pull.Pushed.Count);
        var created = Assert.IsType<FileAttachmentComponent>(Assert.IsType<BotComponentInsert>(Assert.Single(pull.Pushed[1].BotComponentChanges)).Component);
        Assert.Equal((pull.Synced.DisplayName, LocalDescription), (created.DisplayName, created.Description));
        Assert.NotEqual(pull.Synced.Id, created.Id);
        Assert.Equal(LocalContent, await pull.Accessor.ReadStringAsync(pull.PayloadPath, CancellationToken.None));
    }

    [Fact]
    public async Task Push_KnowledgeFileReplacedInTheCloudUnderAnotherSchemaAfterALocalEdit_AsksToGetTheLatestChangesInsteadOfReplacingItEvenWhenOverwriting()
    {
        var pull = await DescribeTheKnowledgeFileAsync(synced => ReplaceInTheCloud(synced.WithSchemaName($"{synced.SchemaNameString}_r2")), lastSyncedContent: "synced rules");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => pull.PushAsync(overwriteRemoteChanges: true));

        Assert.Contains("Get the latest changes", refusal.Message);
        Assert.Single(pull.Pushed);
        Assert.Equal(LocalContent, await pull.Accessor.ReadStringAsync(pull.PayloadPath, CancellationToken.None));
        Assert.Equal(KnowledgeSidecar(pull.Synced.DisplayName, LocalDescription), await pull.Accessor.ReadStringAsync(pull.SidecarPath, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Push_KnowledgeFileMissingInTheEnvironmentWithoutAChangeInTheCloud_ReportsTheMissingComponentWithoutRetrying(bool overwriteRemoteChanges)
    {
        var pull = await DescribeTheKnowledgeFileAsync(_ => []);

        await Assert.ThrowsAsync<DataverseNotFoundException>(() => pull.PushAsync(overwriteRemoteChanges));

        Assert.Single(pull.Pushed);
    }

    [Theory]
    [InlineData(DeletedKnowledgeFileEdits.Refuse)]
    [InlineData(DeletedKnowledgeFileEdits.Keep)]
    public async Task Pull_KnowledgeFileDeletedInTheCloudAfterALocalDescriptionEditOfItsUndownloadedPayload_StopsBeforeWritingAnythingBecauseItCannotBeKept(DeletedKnowledgeFileEdits deletedKnowledgeFileEdits)
    {
        var pull = await DescribeTheKnowledgeFileAsync(DeleteInTheCloud, downloaded: false);

        await AssertPullStopsBeforeWritingAnythingAsync(pull, deletedKnowledgeFileEdits, pull.SidecarPath, canBeKept: false);
    }

    [Fact]
    public async Task Pull_KnowledgeFileDeletedInTheCloudAfterALocalDescriptionEditOfItsUndownloadedPayload_RemovesItsSidecarWhenTheLocalEditIsDiscarded()
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(DeleteInTheCloud, downloaded: false, deletedKnowledgeFileEdits: DeletedKnowledgeFileEdits.Discard);

        Assert.False(pull.Accessor.Exists(pull.SidecarPath));
        Assert.Empty((await GetLocalChangesAsync(pull.Synchronizer, pull.Workspace)).BotComponentChanges);
    }

    [Theory]
    [InlineData("synced rules", true)]
    [InlineData(LocalContent, false)]
    public async Task Pull_KnowledgeFileDeletedInTheCloud_AsksFirstOnlyWhenItsContentChangedLocallySinceItsLastSync(string lastSyncedContent, bool contentChanged)
    {
        var pull = await DescribeTheKnowledgeFileAsync(DeleteInTheCloud, describeLocally: false, lastSyncedContent: lastSyncedContent);

        var refusal = await Record.ExceptionAsync(() => pull.PullAsync(DeletedKnowledgeFileEdits.Refuse));

        IReadOnlyList<string> expected = contentChanged ? [pull.PayloadPath.ToString()] : [];
        Assert.Equal(expected, refusal is DeletedKnowledgeFileEditsException { CanBeKept: true } failure ? failure.FilePaths : []);
        Assert.Equal(contentChanged, pull.Accessor.Exists(pull.PayloadPath));
    }

    [Theory]
    [InlineData(DeletedKnowledgeFileEdits.Refuse, false)]
    [InlineData(DeletedKnowledgeFileEdits.Discard, true)]
    public async Task Pull_KnowledgeFileReplacedInTheCloudUnderTheSameName_KeepsTheReplacementFilesWithNothingToPushWhenNoLocalEditIsLost(DeletedKnowledgeFileEdits deletedKnowledgeFileEdits, bool describeLocally)
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(ReplaceInTheCloud, describeLocally, deletedKnowledgeFileEdits: deletedKnowledgeFileEdits);

        Assert.NotEqual(pull.Synced.Id, ReadCachedKnowledgeFile(pull.Accessor).Id);
        Assert.Equal("cloud rules", await pull.Accessor.ReadStringAsync(pull.PayloadPath, CancellationToken.None));
        Assert.Empty((await GetLocalChangesAsync(pull.Synchronizer, pull.Workspace)).BotComponentChanges);
    }

    [Theory]
    [InlineData(DeletedKnowledgeFileEdits.Refuse, true, null)]
    [InlineData(DeletedKnowledgeFileEdits.Keep, true, null)]
    [InlineData(DeletedKnowledgeFileEdits.Refuse, false, "synced rules")]
    [InlineData(DeletedKnowledgeFileEdits.Keep, false, "synced rules")]
    public async Task Pull_KnowledgeFileReplacedInTheCloudUnderTheSameNameAfterALocalEdit_StopsBeforeWritingAnythingBecauseTheReplacementTakesItsPlace(DeletedKnowledgeFileEdits deletedKnowledgeFileEdits, bool describeLocally, string? lastSyncedContent)
    {
        var pull = await DescribeTheKnowledgeFileAsync(ReplaceInTheCloud, describeLocally, lastSyncedContent: lastSyncedContent);

        await AssertPullStopsBeforeWritingAnythingAsync(pull, deletedKnowledgeFileEdits, pull.PayloadPath, canBeKept: false);
    }

    [Theory]
    [InlineData(DeletedKnowledgeFileEdits.Refuse)]
    [InlineData(DeletedKnowledgeFileEdits.Keep)]
    public async Task Pull_KnowledgeFileAddedLocallyAndInTheCloudUnderTheSameName_StopsBeforeWritingAnythingBecauseTheCloudFileTakesItsPlace(DeletedKnowledgeFileEdits deletedKnowledgeFileEdits)
    {
        var pull = await DescribeTheKnowledgeFileAsync(AddParkingInTheCloud, describeLocally: false, addParkingLocally: true);

        await AssertPullStopsBeforeWritingAnythingAsync(pull, deletedKnowledgeFileEdits, ParkingPayloadPath, canBeKept: false);
    }

    [Fact]
    public async Task Pull_KnowledgeFileAddedLocallyAndInTheCloudUnderTheSameName_IsReplacedByTheCloudFileWhenTheLocalOneIsDiscarded()
    {
        var pull = await PullAfterDescribingTheKnowledgeFileAsync(AddParkingInTheCloud, describeLocally: false, deletedKnowledgeFileEdits: DeletedKnowledgeFileEdits.Discard, addParkingLocally: true);

        Assert.False(pull.Accessor.Exists(ParkingSidecarPath));
        Assert.Equal("cloud rules", await pull.Accessor.ReadStringAsync(ParkingPayloadPath, CancellationToken.None));
        Assert.Empty((await GetLocalChangesAsync(pull.Synchronizer, pull.Workspace)).BotComponentChanges);
    }

    [Fact]
    public async Task Pull_DiscardingOnlySomeOfTheLocalChangesTheCloudPutsAtRisk_StopsBeforeWritingAnythingNamingThemAll()
    {
        var pull = await DescribeTheKnowledgeFileAsync(synced => [.. DeleteInTheCloud(synced), .. AddParkingInTheCloud(synced)], addParkingLocally: true);
        var before = SnapshotFiles(pull.Accessor);

        var refusal = await Assert.ThrowsAsync<DeletedKnowledgeFileEditsException>(() => pull.PullAsync(DeletedKnowledgeFileEdits.Discard, ImmutableArray.Create(pull.PayloadPath.ToString())));

        Assert.Equal([pull.PayloadPath.ToString(), ParkingPayloadPath.ToString()], refusal.FilePaths);
        Assert.False(refusal.CanBeKept);
        Assert.Equal(before, SnapshotFiles(pull.Accessor));
    }

    [Fact]
    public async Task Pull_DiscardingEveryLocalChangeTheCloudPutsAtRisk_DeletesOrReplacesThemAll()
    {
        var pull = await DescribeTheKnowledgeFileAsync(synced => [.. DeleteInTheCloud(synced), .. AddParkingInTheCloud(synced)], addParkingLocally: true);

        await pull.PullAsync(DeletedKnowledgeFileEdits.Discard, ImmutableArray.Create(pull.PayloadPath.ToString(), ParkingPayloadPath.ToString()));

        Assert.False(pull.Accessor.Exists(pull.SidecarPath));
        Assert.False(pull.Accessor.Exists(pull.PayloadPath));
        Assert.False(pull.Accessor.Exists(ParkingSidecarPath));
        Assert.Equal("cloud rules", await pull.Accessor.ReadStringAsync(ParkingPayloadPath, CancellationToken.None));
        Assert.Empty((await GetLocalChangesAsync(pull.Synchronizer, pull.Workspace)).BotComponentChanges);
    }

    [Theory]
    [InlineData("Parking rules. Use for questions about parking fees.", "Parking rules. Use for questions about parking fees.")]
    [InlineData("", "This knowledge source searches information contained in parking.txt")]
    [InlineData(null, "This knowledge source searches information contained in parking.txt")]
    public async Task GetLocalChanges_NewKnowledgeFileWithASidecar_IsCreatedWithItsAuthoredDescription(string? authoredDescription, string expectedDescription)
    {
        var (_, _, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        await accessor.WriteAsync(new AgentFilePath("capabilities/knowledge/files/parking.txt"), FileBytes, CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("capabilities/knowledge/files/parkingtxt_r1.mcs.yml"), KnowledgeSidecar("parking.txt", authoredDescription), CancellationToken.None);
        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, read, new Mock<ISyncDataverseClient>().Object, MakeSyncInfo(), CancellationToken.None);

        var created = Assert.IsType<FileAttachmentComponent>(Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentInsert>()).Component);
        Assert.Equal("Default_draft_rxzs_q.file.parkingtxt_r1", created.SchemaNameString);
        Assert.Equal(expectedDescription, created.Description);
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

    private static string KnowledgeSidecar(string? fileName, string? description) => $"mcs.metadata:\n  componentName: {fileName}\n" + (description is null ? string.Empty : $"  description: \"{description}\"\n");

    private static BotComponentChange[] DescribeInTheCloud(FileAttachmentComponent synced) => [new BotComponentUpdate(synced.WithDescription(CloudDescription).WithVersion(synced.Version + 1))];

    private static BotComponentChange[] DeleteInTheCloud(FileAttachmentComponent synced) => [new BotComponentDelete(synced.Id, synced.Version)];

    private static BotComponentChange[] ReplaceInTheCloud(FileAttachmentComponent synced)
    {
        var replacement = synced.ToBuilder();
        replacement.Id = Guid.NewGuid();
        replacement.Description = CloudDescription;
        return [new BotComponentDelete(synced.Id, synced.Version), new BotComponentInsert(replacement.Build())];
    }

    private static BotComponentChange[] AddParkingInTheCloud(FileAttachmentComponent synced) => [new BotComponentInsert(KnowledgeFileTestFixtures.CreateFileComponent($"{synced.SchemaNameString}_parking", ParkingFileName, Guid.NewGuid()))];

    private sealed record KnowledgeFilePull(FileAttachmentComponent Synced, InMemoryFileAccessor Accessor, WorkspaceSynchronizer Synchronizer, DirectoryPath Workspace, AgentFilePath SidecarPath, AgentFilePath PayloadPath, DefinitionBase Local, Mock<ISyncDataverseClient> Dataverse, List<WorkspaceDiagnostic> Conflicts, List<PvaComponentChangeSet> Pushed)
    {
        public Task PullAsync(DeletedKnowledgeFileEdits deletedKnowledgeFileEdits, ImmutableArray<string>? approvedKnowledgeFileDeletions = null) => Synchronizer.PullExistingChangesAsync(Workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), Local, Dataverse.Object, MakeSyncInfo(), CancellationToken.None, conflicts: Conflicts, deletedKnowledgeFileEdits: deletedKnowledgeFileEdits, approvedKnowledgeFileDeletions: approvedKnowledgeFileDeletions);

        public Task PushAsync(bool overwriteRemoteChanges) => Synchronizer.PushLocalChangesAsync(Workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), Local, Dataverse.Object, MakeSyncInfo(), cloudFlowMetadata: null, ImmutableArray<SyncDataverseClient.AIPromptMetadata>.Empty, CancellationToken.None, overwriteRemoteChanges: overwriteRemoteChanges);
    }

    private static async Task<KnowledgeFilePull> PullAfterDescribingTheKnowledgeFileAsync(Func<FileAttachmentComponent, BotComponentChange[]> changesInTheCloud, bool describeLocally = true, bool downloaded = true, DeletedKnowledgeFileEdits deletedKnowledgeFileEdits = DeletedKnowledgeFileEdits.Keep, bool addParkingLocally = false)
    {
        var pull = await DescribeTheKnowledgeFileAsync(changesInTheCloud, describeLocally, downloaded, addParkingLocally: addParkingLocally);
        await pull.PullAsync(deletedKnowledgeFileEdits);
        return pull;
    }

    private static async Task<KnowledgeFilePull> DescribeTheKnowledgeFileAsync(Func<FileAttachmentComponent, BotComponentChange[]> changesInTheCloud, bool describeLocally = true, bool downloaded = true, string? lastSyncedContent = null, bool addParkingLocally = false)
    {
        var infrastructure = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var (_, definition, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger", infrastructure);
        var synced = definition.Components.OfType<FileAttachmentComponent>().Single();
        var sidecarPath = accessor.ListFiles("capabilities/knowledge/files", "*.mcs.yml").Single();
        var payloadPath = new AgentFilePath($"capabilities/knowledge/files/{synced.DisplayName}");
        if (lastSyncedContent is not null)
        {
            await accessor.WriteAsync(payloadPath, lastSyncedContent, CancellationToken.None);
            using var baseline = accessor.OpenWrite(new AgentFilePath(".mcs/.knowledge-sync.json"));
            JsonSerializer.Serialize(baseline, new Dictionary<string, string> { [synced.Id.Value.ToString("N")] = Convert.ToBase64String(SHA256.HashData(WorkspaceBytes.Read(accessor, payloadPath))) });
        }

        if (downloaded)
        {
            await accessor.WriteAsync(payloadPath, LocalContent, CancellationToken.None);
        }

        if (describeLocally)
        {
            await accessor.WriteAsync(sidecarPath, KnowledgeSidecar(synced.DisplayName, LocalDescription), CancellationToken.None);
        }

        if (addParkingLocally)
        {
            await accessor.WriteAsync(ParkingPayloadPath, LocalContent, CancellationToken.None);
            await accessor.WriteAsync(ParkingSidecarPath, KnowledgeSidecar(ParkingFileName, LocalDescription), CancellationToken.None);
        }

        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        infrastructure.island.Setup(island => island.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new PvaComponentChangeSet(changesInTheCloud(synced), null, "after-cloud-change"));
        var pushed = new List<PvaComponentChangeSet>();
        infrastructure.island
            .Setup(island => island.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                pushed.Add(incoming);
                return pushed.Count == 1 ? throw new DataverseNotFoundException("ObjectDoesNotExist", "0x80040217", Guid.NewGuid().ToString(), $"Entity 'botcomponent' With Id = {synced.Id} Does Not Exist", new HttpRequestException("Not Found")) : Task.FromResult(EnvironmentVariableTestFixtures.CreateChangeSet("after-push", EnvironmentVariableTestFixtures.ConfirmComponentChanges(incoming)));
            });
        var dataverse = KnowledgeFileTestFixtures.CreateStreamingMock(destination => KnowledgeFileTestFixtures.SeedContent(destination, "cloud rules"));
        return new KnowledgeFilePull(synced, accessor, synchronizer, workspace, sidecarPath, payloadPath, local, dataverse, [], pushed);
    }

    internal static List<(string Path, string Content)> SnapshotFiles(InMemoryFileAccessor accessor) => accessor.Files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => (file.Key, Convert.ToBase64String(file.Value))).ToList();

    private static async Task AssertPullStopsBeforeWritingAnythingAsync(KnowledgeFilePull pull, DeletedKnowledgeFileEdits deletedKnowledgeFileEdits, AgentFilePath expectedFilePath, bool canBeKept)
    {
        var before = SnapshotFiles(pull.Accessor);

        var refusal = await Assert.ThrowsAsync<DeletedKnowledgeFileEditsException>(() => pull.PullAsync(deletedKnowledgeFileEdits));

        Assert.Equal([expectedFilePath.ToString()], refusal.FilePaths);
        Assert.Equal(canBeKept, refusal.CanBeKept);
        Assert.Equal(before, SnapshotFiles(pull.Accessor));
    }

    private static async Task<PvaComponentChangeSet> GetLocalChangesAsync(WorkspaceSynchronizer synchronizer, DirectoryPath workspace) => (await synchronizer.GetLocalChangesAsync(workspace, await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true), new Mock<ISyncDataverseClient>().Object, MakeSyncInfo(), CancellationToken.None)).Item1;

    private static DefinitionBase ReadCloudCache(InMemoryFileAccessor accessor) => WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor) ?? throw new Xunit.Sdk.XunitException("Expected a cloud cache.");

    private static FileAttachmentComponent ReadCachedKnowledgeFile(InMemoryFileAccessor accessor) => Assert.Single(ReadCloudCache(accessor).Components.OfType<FileAttachmentComponent>());

    private static async Task<(FileAttachmentComponent Synced, DefinitionBase Read, PvaComponentChangeSet ChangeSet)> GetLocalChangesAfterDescribingTheKnowledgeFileAsync(string? description)
    {
        var (_, definition, accessor, synchronizer, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var synced = definition.Components.OfType<FileAttachmentComponent>().Single();
        await accessor.WriteAsync(accessor.ListFiles("capabilities/knowledge/files", "*.mcs.yml").Single(), KnowledgeSidecar(synced.DisplayName, description), CancellationToken.None);
        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);
        var (changeSet, _) = await synchronizer.GetLocalChangesAsync(workspace, read, new Mock<ISyncDataverseClient>().Object, MakeSyncInfo(), CancellationToken.None);
        return (synced, read, changeSet);
    }

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
