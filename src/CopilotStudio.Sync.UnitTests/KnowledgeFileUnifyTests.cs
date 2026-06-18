// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class KnowledgeFileUnifyTests
{
    private static readonly byte[] FileBytes = Encoding.UTF8.GetBytes("col1,col2\n1,2\n");

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
        dataverse.Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        var downloadedFolders = new List<string>();
        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, BotComponentId, string, CancellationToken>((folder, _, _, _) => downloadedFolders.Add(folder))
            .Returns(Task.CompletedTask);

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, dataverse.Object, schemaNames: null, CancellationToken.None);

        var info = Assert.Single(downloaded);
        Assert.Equal(fileComponent.SchemaNameString, info.SchemaName);
        Assert.Equal($"capabilities/knowledge/files/{fileComponent.DisplayName}", info.RelativePath);
        Assert.Single(downloadedFolders);
        Assert.EndsWith("capabilities/knowledge/files", downloadedFolders[0].Replace('\\', '/').TrimEnd('/'));
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_All_SkipsMissingFileAttachment()
    {
        var (_, _, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(System.Net.HttpStatusCode.NotFound, "{\"error\":{\"message\":\"No file attachment found for attribute: filedata\"}}"));

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, dataverse.Object, schemaNames: null, CancellationToken.None);

        Assert.Empty(downloaded);
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_SelectiveKnownSchema_ThrowsMissingFileAttachment()
    {
        var (_, definition, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
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

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace, dataverse.Object, schemaNames: new[] { "cr1d_foodlogger.file.does_not_exist" }, CancellationToken.None);

        Assert.Empty(downloaded);
        dataverse.Verify(d => d.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_SelectiveKnownSchema_DownloadsThatFile()
    {
        var (_, definition, _, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var fileComponent = definition.Components.OfType<FileAttachmentComponent>().Single();

        var dataverse = new Mock<ISyncDataverseClient>();
        dataverse.Setup(d => d.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(
            workspace, dataverse.Object, schemaNames: new[] { fileComponent.SchemaNameString }, CancellationToken.None);

        Assert.Single(downloaded);
        dataverse.Verify(d => d.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), fileComponent.DisplayName!, It.IsAny<CancellationToken>()), Times.Once);
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
        dataverse.Setup(d => d.UploadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, string, CancellationToken>((_, _, fileName, _) => uploadedFileNames.Add(fileName))
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

        var uploaded = await synchronizer.UploadKnowledgeFilesAsync(workspace, dataverse.Object, CancellationToken.None);

        Assert.Empty(uploaded);
        dataverse.Verify(
            d => d.UploadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
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

        var insert = Assert.Single(
            changeSet.BotComponentChanges
                .OfType<BotComponentInsert>()
                .Where(i => i.Component is FileAttachmentComponent fac && fac.DisplayName == "NewKb.txt"));
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
}
