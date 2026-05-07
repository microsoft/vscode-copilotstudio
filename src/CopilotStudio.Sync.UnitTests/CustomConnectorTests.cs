namespace Microsoft.CopilotStudio.Sync.UnitTests
{
    using Microsoft.Agents.ObjectModel;
    using Microsoft.Agents.Platform.Content;
    using Microsoft.CopilotStudio.McsCore;
    using Microsoft.CopilotStudio.Sync;
    using Microsoft.CopilotStudio.Sync.Dataverse;
    using Moq;
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Xunit;

    public class CustomConnectorTests : IDisposable
    {
        private readonly string _tempRoot;

        public CustomConnectorTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "mcs-conn-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot))
                {
                    Directory.Delete(_tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_NoConnectorsFolder_ReturnsEmptyResult()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Empty(result.PushedRowIds);
            Assert.Empty(result.NewlyCreatedConnectorNames);
            dataverse.Verify(d => d.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_FolderExistsButNoMetadata_ReturnsEmptyResult()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            Directory.CreateDirectory(Path.Combine(workspace.ToString(), "connectors", "Empty-" + Guid.NewGuid()));
            var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Empty(result.PushedRowIds);
            Assert.Empty(result.NewlyCreatedConnectorNames);
            dataverse.Verify(d => d.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_NewlyCreatedConnector_IsReportedByDisplayName()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            var connectorId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "MyConn", connectorId, displayName: "My Display", name: "myconn", internalId: "myconn-internal");

            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Single(result.NewlyCreatedConnectorNames);
            Assert.Equal("My Display", result.NewlyCreatedConnectorNames.Single());
            Assert.Single(result.PushedRowIds);
            Assert.Equal(connectorId, result.PushedRowIds["myconn-internal"]);
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_UpdatedConnector_IsNotInNewlyCreatedNames()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            var connectorId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "Existing", connectorId, displayName: "Existing Display", name: "existing", internalId: "existing-internal");

            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Empty(result.NewlyCreatedConnectorNames);
            Assert.Single(result.PushedRowIds);
            Assert.Equal(connectorId, result.PushedRowIds["existing-internal"]);
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_FallsBackToName_WhenDisplayNameMissing()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            var connectorId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "OnlyName", connectorId, displayName: null, name: "fallback-name", internalId: "fallback-internal");

            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Equal("fallback-name", result.NewlyCreatedConnectorNames.Single());
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_FallsBackToConnectorId_WhenNameAndDisplayNameMissing()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            var connectorId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "NoName", connectorId, displayName: null, name: null, internalId: "no-name-internal");

            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Equal(connectorId.ToString(), result.NewlyCreatedConnectorNames.Single());
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_RecoversConnectorIdFromFolderName_WhenMetadataMissingId()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            var connectorId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "FolderId", connectorIdInMetadata: Guid.Empty, folderConnectorId: connectorId,
                displayName: "Folder Display", name: "folder-name", internalId: "folder-internal");

            CustomConnectorMetadata? captured = null;
            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
                .Callback<CustomConnectorMetadata, CancellationToken>((m, _) => captured = m)
                .ReturnsAsync(true);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Equal(connectorId, captured!.ConnectorId);
            Assert.Equal(connectorId, result.PushedRowIds["folder-internal"]);
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_CreatesAndUpdates_PartitionsCorrectly()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();

            var newId = Guid.NewGuid();
            var existingId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "NewOne", newId, displayName: "New One", name: "new-one", internalId: "new-one-internal");
            WriteConnectorMetadata(workspace, "OldOne", existingId, displayName: "Old One", name: "old-one", internalId: "old-one-internal");

            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.UpsertConnectorAsync(It.Is<CustomConnectorMetadata>(c => c.ConnectorId == newId), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            dataverse
                .Setup(d => d.UpsertConnectorAsync(It.Is<CustomConnectorMetadata>(c => c.ConnectorId == existingId), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Equal(new[] { "New One" }, result.NewlyCreatedConnectorNames);
            Assert.Equal(2, result.PushedRowIds.Count);
            Assert.Equal(newId, result.PushedRowIds["new-one-internal"]);
            Assert.Equal(existingId, result.PushedRowIds["old-one-internal"]);
        }

        [Fact]
        public async Task PushChangesetAsync_PropagatesNewlyCreatedConnectors_ToResult()
        {
            var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();

            var fileAccessor = fileAccessorFactory.Create(workspace);
            WorkspaceSynchronizer.WriteCloudCache(fileAccessor, new BotDefinition());
            await fileAccessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "token-0", CancellationToken.None);

            var emptyChangeset = new PvaComponentChangeSet(null, null, "token-1");
            mockIsland
                .Setup(x => x.SaveChangesAsync(
                    It.IsAny<AuthoringOperationContextBase>(),
                    It.IsAny<PvaComponentChangeSet>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(emptyChangeset);

            var connectorId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "Pushed", connectorId, displayName: "Pushed Display", name: "pushed", internalId: "pushed-internal");

            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.UpsertConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            dataverse
                .Setup(d => d.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<CustomConnectorMetadata>());

            var result = await synchronizer.PushChangesetAsync(
                workspace,
                ComponentWriterDefensiveTests.CreateMockOperationContext(),
                emptyChangeset,
                dataverse.Object,
                agentId: Guid.NewGuid(),
                cloudFlowMetadata: null,
                CancellationToken.None);

            Assert.Equal(0, result.UploadedKnowledgeFileCount);
            Assert.Equal(new[] { "Pushed Display" }, result.NewlyCreatedCustomConnectors);
        }

        [Fact]
        public void ExtractConnectorInternalId_NullOrWhitespace_ReturnsNull()
        {
            Assert.Null(SyncDataverseClient.ExtractConnectorInternalId(null));
            Assert.Null(SyncDataverseClient.ExtractConnectorInternalId(""));
            Assert.Null(SyncDataverseClient.ExtractConnectorInternalId("   "));
        }

        [Fact]
        public void ExtractConnectorInternalId_WithoutSlash_ReturnsAsIs()
        {
            Assert.Equal("bare-id", SyncDataverseClient.ExtractConnectorInternalId("bare-id"));
        }

        [Fact]
        public void ExtractConnectorInternalId_WithSlashes_ReturnsLastSegment()
        {
            Assert.Equal("my-id", SyncDataverseClient.ExtractConnectorInternalId("/providers/Microsoft.PowerApps/apis/my-id"));
            Assert.Equal("only", SyncDataverseClient.ExtractConnectorInternalId("a/b/only"));
        }

        [Fact]
        public void ExtractConnectorInternalId_TrailingSlash_ReturnsNull()
        {
            Assert.Null(SyncDataverseClient.ExtractConnectorInternalId("a/b/"));
        }

        private DirectoryPath NewWorkspace()
        {
            var path = Path.Combine(_tempRoot, "ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new DirectoryPath(path.Replace('\\', '/'));
        }

        private static void WriteConnectorMetadata(
            DirectoryPath workspace,
            string folderPrefix,
            Guid connectorId,
            string? displayName,
            string? name,
            string? internalId)
            => WriteConnectorMetadata(workspace, folderPrefix, connectorIdInMetadata: connectorId, folderConnectorId: connectorId, displayName, name, internalId);

        private static void WriteConnectorMetadata(
            DirectoryPath workspace,
            string folderPrefix,
            Guid connectorIdInMetadata,
            Guid folderConnectorId,
            string? displayName,
            string? name,
            string? internalId)
        {
            var folder = Path.Combine(workspace.ToString(), "connectors", $"{folderPrefix}-{folderConnectorId}");
            Directory.CreateDirectory(folder);

            var meta = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["displayname"] = displayName,
                ["connectorinternalid"] = internalId,
            };

            if (connectorIdInMetadata != Guid.Empty)
            {
                meta["connectorid"] = connectorIdInMetadata;
            }

            var json = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(folder, "metadata.yml"), json, Encoding.UTF8);
        }
    }
}