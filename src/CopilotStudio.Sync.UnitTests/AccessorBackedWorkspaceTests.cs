// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AccessorBackedWorkspaceTests
{
    [Fact]
    public async Task WriteCustomConnectors_RemovedFromCloud_PrunesFolderButLeavesCloudBaselinesIntact()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/connector-prune-inmemory/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var staleRowId = Guid.NewGuid();
        var stalePath = new AgentFilePath($"connectors/OldConnector-{staleRowId}/metadata.yml");
        await fileAccessor.WriteAsync(stalePath, $"connectorId: {staleRowId}\nname: OldConnector", CancellationToken.None);

        await fileAccessor.WriteAsync(
            new AgentFilePath(".mcs/.connectors-download.json"),
            $"{{\"{staleRowId:N}\":\"5\"}}",
            CancellationToken.None);
        await fileAccessor.WriteAsync(
            new AgentFilePath(".mcs/.connectors-sync.json"),
            $"{{\"{staleRowId:N}\":\"hash\"}}",
            CancellationToken.None);

        var connectionReference = new ConnectionReference(
            connectionReferenceLogicalName: "cr1.shared_test." + Guid.NewGuid().ToString("N"),
            connectionId: string.Empty,
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_test");
        var cloudFlowMetadata = new CloudFlowMetadata
        {
            Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
            ConnectionReferences = ImmutableArray.Create(connectionReference),
        };

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!;
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, botEntity, "token-1"));

        var currentRowId = Guid.NewGuid();
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());

        mockDataverse
            .Setup(x => x.GetConnectorVersionsByInternalIdsAsync(It.IsAny<System.Collections.Generic.IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new CustomConnectorMetadata { ConnectorId = currentRowId, ConnectorInternalId = "shared_test", VersionNumber = 5L, Name = "TestConnector" } });
        mockDataverse
            .Setup(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<System.Collections.Generic.IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new CustomConnectorMetadata { ConnectorId = currentRowId, ConnectorInternalId = "shared_test", VersionNumber = 5L, Name = "TestConnector" } });

        await synchronizer.SyncWorkspaceAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            null,
            false,
            mockDataverse.Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            cloudFlowMetadata,
            CancellationToken.None);

        Assert.False(fileAccessor.Exists(stalePath), "connector removed from the cloud must not survive in the accessor-backed workspace");

        var downloadBaseline = await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/.connectors-download.json"), CancellationToken.None);
        var contentBaseline = await fileAccessor.ReadStringAsync(new AgentFilePath(".mcs/.connectors-sync.json"), CancellationToken.None);
        Assert.Contains(staleRowId.ToString("N"), downloadBaseline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(staleRowId.ToString("N"), contentBaseline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteCustomConnectors_ReAddedConnector_IsRedownloadedEvenWithStaleBaseline()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/connector-readd-inmemory/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var rowId = Guid.NewGuid();

        await fileAccessor.WriteAsync(
            new AgentFilePath(".mcs/.connectors-download.json"),
            $"{{\"{rowId:N}\":\"5\"}}",
            CancellationToken.None);

        var connectionReference = new ConnectionReference(
            connectionReferenceLogicalName: "cr1.shared_test." + Guid.NewGuid().ToString("N"),
            connectionId: string.Empty,
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_test");
        var cloudFlowMetadata = new CloudFlowMetadata
        {
            Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
            ConnectionReferences = ImmutableArray.Create(connectionReference),
        };

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!;
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, botEntity, "token-1"));

        var connector = new CustomConnectorMetadata { ConnectorId = rowId, ConnectorInternalId = "shared_test", VersionNumber = 5L, Name = "TestConnector" };
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        mockDataverse
            .Setup(x => x.GetConnectorVersionsByInternalIdsAsync(It.IsAny<System.Collections.Generic.IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { connector });
        mockDataverse
            .Setup(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<System.Collections.Generic.IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { connector });

        await synchronizer.SyncWorkspaceAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            null,
            false,
            mockDataverse.Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            cloudFlowMetadata,
            CancellationToken.None);

        Assert.True(
            fileAccessor.ListFiles("connectors").Any(file => file.ToString().Contains(rowId.ToString())),
            "a matching version baseline must not suppress the download when the connector folder is absent");
        mockDataverse.Verify(
            x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<System.Collections.Generic.IEnumerable<string>>(), false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_WritesContentThroughAccessor_SoUploadCanReadItBack()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-roundtrip-inmemory/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var componentId = Guid.NewGuid();
        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", componentId);
        var cloudCache = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!)
            .WithComponents(new BotComponentBase[] { fileComponent });
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, cloudCache);

        var payload = Encoding.UTF8.GetBytes("knowledge-payload");
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.As<IStreamingKnowledgeFileClient>().Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns<System.IO.Stream, BotComponentId, CancellationToken>((destination, _, _) =>
            {
                destination.Write(payload, 0, payload.Length);
                return Task.CompletedTask;
            });

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, mockDataverse.Object, schemaNames: null, CancellationToken.None);

        var info = Assert.Single(downloaded);

        var contentPath = new AgentFilePath(info.RelativePath);
        Assert.True(fileAccessor.Exists(contentPath), "downloaded knowledge content must be written through the workspace accessor");
        Assert.Equal("knowledge-payload", await fileAccessor.ReadStringAsync(contentPath, CancellationToken.None));
    }

    [Fact]
    public async Task DownloadKnowledgeFiles_FailedDownload_LeavesNoEmptyPayloadBehind()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/knowledge-failure-inmemory/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid());
        var cloudCache = new BotDefinition()
            .WithEntity(CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!)
            .WithComponents(new BotComponentBase[] { fileComponent });
        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, cloudCache);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse.As<IStreamingKnowledgeFileClient>().Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DataverseRequestException(
                System.Net.HttpStatusCode.NotFound,
                "{\"error\":{\"message\":\"No file attachment found for attribute: filedata\"}}"));

        var downloaded = await synchronizer.DownloadKnowledgeFilesAsync(workspace, mockDataverse.Object, schemaNames: null, CancellationToken.None);

        Assert.Empty(downloaded);

        Assert.DoesNotContain(
            fileAccessor.ListFiles("capabilities"),
            file => file.ToString().EndsWith("Doc.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetWorkflows_FolderRenameFailsMidway_RollsBackInsteadOfSplittingTheFolder()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/workflow-rename-rollback/");
        var inner = fileAccessorFactory.Create(workspace);

        var workflowId = Guid.NewGuid();
        var sourceFolder = $"workflows/OldName-{workflowId}";
        await inner.WriteAsync(new AgentFilePath($"{sourceFolder}/workflow.json"), "{ \"v\": 1 }", CancellationToken.None);
        await inner.WriteAsync(new AgentFilePath($"{sourceFolder}/metadata.yml"), $"workflowId: {workflowId}\nname: OldName", CancellationToken.None);

        var faulting = new FailOnNthReplaceAccessor(inner, failOnCall: 2);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new WorkflowMetadata { WorkflowId = workflowId, Name = "NewName", ClientData = "{ \"v\": 2 }" } });

        await synchronizer.GetWorkflowsAsync(
            workspace,
            mockDataverse.Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            faulting,
            CancellationToken.None);

        var remaining = inner.ListFiles("workflows").Select(file => file.ToString()).ToList();
        var underSource = remaining.Count(file => file.StartsWith(sourceFolder + "/", StringComparison.OrdinalIgnoreCase));
        var underTarget = remaining.Count(file => file.StartsWith($"workflows/NewName-{workflowId}/", StringComparison.OrdinalIgnoreCase));

        Assert.True(underSource == 0 || underTarget == 0, $"workflow folder was left split across both names: {string.Join(", ", remaining)}");
        Assert.Equal(2, Math.Max(underSource, underTarget));
    }

    private sealed class FailOnNthReplaceAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;
        private readonly int _failOnCall;
        private int _calls;

        public FailOnNthReplaceAccessor(IFileAccessor inner, int failOnCall)
        {
            _inner = inner;
            _failOnCall = failOnCall;
        }

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public System.IO.Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public System.IO.Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public void Delete(AgentFilePath path) => _inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath)
        {
            _calls++;
            if (_calls == _failOnCall)
            {
                throw new System.IO.IOException("simulated move failure");
            }

            _inner.Replace(sourcePath, targetPath);
        }

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
            => _inner.ListFiles(relativeFolder, filePattern);
    }

    [Fact]
    public async Task PullExistingChanges_DownloadAllKnowledgeFiles_WritesPayloadThroughAccessor()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/pull-all-knowledge-inmemory/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!;
        var fileComponent = CreateFileComponent("cr1.file.Doc", "Doc.txt", Guid.NewGuid());

        WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition().WithEntity(botEntity));
        await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-0", CancellationToken.None);

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(fileComponent) }, botEntity, "token-1"));

        var payload = Encoding.UTF8.GetBytes("pulled-knowledge-payload");
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        mockDataverse.As<IStreamingKnowledgeFileClient>().Setup(x => x.DownloadKnowledgeFileAsync(It.IsAny<Stream>(), It.IsAny<BotComponentId>(), It.IsAny<CancellationToken>()))
            .Returns<System.IO.Stream, BotComponentId, CancellationToken>((destination, _, _) =>
            {
                destination.Write(payload, 0, payload.Length);
                return Task.CompletedTask;
            });

        await synchronizer.PullExistingChangesAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            new BotDefinition().WithEntity(botEntity),
            mockDataverse.Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() },
            CancellationToken.None,
            downloadAllKnowledgeFiles: true);

        mockDataverse.Verify(
            x => x.DownloadKnowledgeFileAsync(It.IsAny<string>(), It.IsAny<BotComponentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var written = fileAccessor.ListFiles().Select(file => file.ToString()).Where(file => file.EndsWith("Doc.txt", StringComparison.OrdinalIgnoreCase)).ToList();
        var contentPath = Assert.Single(written);
        Assert.Equal("pulled-knowledge-payload", await fileAccessor.ReadStringAsync(new AgentFilePath(contentPath), CancellationToken.None));
    }

    private static FileAttachmentComponent CreateFileComponent(string schemaName, string displayName, Guid id)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription("desc")
            .ToBuilder();
        builder.Id = id;
        return builder.Build();
    }
}
