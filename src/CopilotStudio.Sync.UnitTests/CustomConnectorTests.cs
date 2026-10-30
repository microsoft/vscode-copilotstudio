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
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Nodes;
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

        private static WorkspaceSynchronizer CreateSynchronizer()
        {
            var fileParser = new SyncMcsFileParser(LspProjectorService.Instance);
            var fileAccessorFactory = new FileAccessorFactory();
            var island = new Mock<IIslandControlPlaneService>();
            var progress = new TestSyncProgress(new List<string>());
            var pathResolver = new LspComponentPathResolver();

            return new WorkspaceSynchronizer(fileParser, fileAccessorFactory, island.Object, progress, pathResolver);
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_NoConnectorsFolder_ReturnsEmptyResult()
        {
            var (synchronizer, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Empty(result.PushedRowIds);
            dataverse.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_FolderExistsButNoMetadata_ReturnsEmptyResult()
        {
            var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = NewWorkspace();
            await fileAccessorFactory.Create(workspace).WriteAsync(
                new AgentFilePath($"connectors/Empty-{Guid.NewGuid()}/readme.txt"),
                "no metadata.yml here",
                CancellationToken.None);
            var dataverse = new Mock<ISyncDataverseClient>(MockBehavior.Strict);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Empty(result.PushedRowIds);
            dataverse.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_RecoversConnectorIdFromFolderName_WhenMetadataMissingId()
        {
            var synchronizer = CreateSynchronizer();
            var workspace = NewWorkspace();
            var connectorId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "FolderId", connectorIdInMetadata: Guid.Empty, folderConnectorId: connectorId,
                displayName: "Folder Display", name: "folder-name", internalId: "folder-internal");

            CustomConnectorMetadata? captured = null;
            var dataverse = CreateStoringDataverse(sent => captured = sent);

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.NotNull(captured);
            Assert.Equal(connectorId, captured.ConnectorId);
            Assert.Equal(connectorId, result.PushedRowIds["folder-internal"]);
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_CreatesAndUpdates_PartitionsByOneBatchedProbe()
        {
            var synchronizer = CreateSynchronizer();
            var workspace = NewWorkspace();

            var newId = Guid.NewGuid();
            var existingId = Guid.NewGuid();
            WriteConnectorMetadata(workspace, "NewOne", newId, displayName: "New One", name: "new-one", internalId: "new-one-internal");
            WriteConnectorMetadata(workspace, "OldOne", existingId, displayName: "Old One", name: "old-one", internalId: "old-one-internal");

            var dataverse = CreateStoringDataverse(existingIds: new[] { existingId });

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Equal(2, result.PushedRowIds.Count);
            Assert.Equal(newId, result.PushedRowIds["new-one-internal"]);
            Assert.Equal(existingId, result.PushedRowIds["old-one-internal"]);
            var created = Assert.Single(result.Created);
            Assert.Equal(ChangeType.Create, created.ChangeType);
            Assert.Equal($"Mcs.Connector.{newId}", created.SchemaName);
            Assert.Equal("New One", created.Name);
            var updated = Assert.Single(result.Updated);
            Assert.Equal(ChangeType.Update, updated.ChangeType);
            Assert.Equal($"Mcs.Connector.{existingId}", updated.SchemaName);
            dataverse.Verify(d => d.GetConnectorVersionsByIdsAsync(It.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(newId) && ids.Contains(existingId)), It.IsAny<CancellationToken>()), Times.Once);
            dataverse.Verify(d => d.SaveConnectorAsync(It.Is<CustomConnectorMetadata>(c => c.ConnectorId == newId), false, It.IsAny<CancellationToken>()), Times.Once);
            dataverse.Verify(d => d.SaveConnectorAsync(It.Is<CustomConnectorMetadata>(c => c.ConnectorId == existingId), true, It.IsAny<CancellationToken>()), Times.Once);
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

        [Fact]
        public async Task PushCustomConnectorsAsync_RejectsPathTraversal_DoesNotExfiltrateFileOutsideConnectorFolder()
        {
            var synchronizer = CreateSynchronizer();
            var workspace = NewWorkspace();
            var connectorId = Guid.NewGuid();
            var folderPrefix = "Traversal";
            WriteConnectorMetadata(workspace, folderPrefix, connectorId, displayName: "Traversal", name: "traversal", internalId: "traversal-internal");

            // Place a file under the workspace but outside the connector's own folder.
            var secretsPath = Path.Combine(workspace.ToString(), "secrets.txt");
            const string secretContents = "TOP-SECRET-API-KEY";
            File.WriteAllText(secretsPath, secretContents, Encoding.UTF8);

            var connectorFolder = Path.Combine(workspace.ToString(), "connectors", $"{folderPrefix}-{connectorId}");
            var metaPath = Path.Combine(connectorFolder, "metadata.yml");
            var traversal = $"connectors/{folderPrefix}-{connectorId}/../../secrets.txt";
            var meta = new Dictionary<string, object?>
            {
                ["connectorid"] = connectorId,
                ["name"] = "traversal",
                ["displayname"] = "Traversal",
                ["connectorinternalid"] = "traversal-internal",
                ["openapidefinition"] = traversal,
            };
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

            CustomConnectorMetadata? captured = null;
            var dataverse = CreateStoringDataverse(sent => captured = sent);

            await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.NotNull(captured);
            Assert.NotEqual(secretContents, captured.OpenApiDefinition);
            Assert.Equal(traversal, captured.OpenApiDefinition);
        }

        [Fact]
        public async Task PushCustomConnectorsAsync_SaveFails_ReportsTheFailureKeepsItsBaselineAndPushesTheRest()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var failingId = Guid.NewGuid();
            var workingId = Guid.NewGuid();
            await WriteConnectorAsync(files, "Failing", failingId);
            await WriteConnectorAsync(files, "Working", workingId);
            var dataverse = CreateStoringDataverse();
            dataverse
                .Setup(d => d.SaveConnectorAsync(It.Is<CustomConnectorMetadata>(c => c.ConnectorId == failingId), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("denied"));

            var result = await synchronizer.PushCustomConnectorsAsync(workspace, dataverse.Object, CancellationToken.None);

            Assert.Equal($"Mcs.Connector.{failingId}", Assert.Single(result.Failed).SchemaName);
            Assert.Equal($"Mcs.Connector.{workingId}", Assert.Single(result.Created).SchemaName);
            Assert.False(result.PushedRowIds.ContainsKey("Failing-internal"));
            Assert.Equal(workingId, result.PushedRowIds["Working-internal"]);
            var baselines = await files.ReadStringAsync(new AgentFilePath(".mcs/.connectors-sync.json"), CancellationToken.None);
            Assert.DoesNotContain(failingId.ToString("N"), baselines, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(workingId.ToString("N"), baselines, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PushChangesetAsync_LocalConnectorEdit_IsNotPushedASecondTime()
        {
            var (synchronizer, fileAccessorFactory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = new DirectoryPath($"c:/test/connector-changeset-{Guid.NewGuid():N}/");
            var files = fileAccessorFactory.Create(workspace);
            WorkspaceSynchronizer.WriteCloudCache(files, new BotDefinition());
            await WriteConnectorAsync(files, "Contoso", Guid.NewGuid());
            var changeset = new PvaComponentChangeSet(null, CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: testbot"), "token-2");
            island
                .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(changeset);
            var dataverse = new Mock<ISyncDataverseClient>();

            await synchronizer.PushChangesetAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), changeset, dataverse.Object, Guid.NewGuid(), null, default, CancellationToken.None);

            dataverse.Verify(d => d.GetConnectorVersionsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
            dataverse.Verify(d => d.SaveConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ChangeDetection_SyncedConnector_ReportsNoChanges()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var connectorId = Guid.NewGuid();
            await SyncConnectorAsync(synchronizer, files, workspace, "Contoso", connectorId, version: 5);

            Assert.Empty(await LocalConnectorChangesAsync(synchronizer, workspace));
            Assert.Empty(await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse(RemoteConnector("Contoso", connectorId, version: 5)).Object));
        }

        [Fact]
        public async Task ChangeDetection_EditedLocallyAndInTheCloud_ReportsTheConnectorOnBothSides()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var connectorId = Guid.NewGuid();
            await SyncConnectorAsync(synchronizer, files, workspace, "Contoso", connectorId, version: 5);
            await EditMetadataAsync(files, "Contoso", connectorId, metadata => metadata["iconbrandcolor"] = "#ff0000");
            var portalCopy = RemoteConnector("Contoso", connectorId, version: 6);
            portalCopy.Description = "Edited in the portal";

            var local = Assert.Single(await LocalConnectorChangesAsync(synchronizer, workspace));
            var remote = Assert.Single(await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse(portalCopy).Object));

            Assert.Equal(ChangeType.Update, local.ChangeType);
            Assert.Equal($"connectors/Contoso-{connectorId}/metadata.yml", local.Uri);
            Assert.Equal(ChangeType.Update, remote.ChangeType);
            Assert.Equal(local.SchemaName, remote.SchemaName);
            Assert.Contains("Edited in the portal", remote.RemoteContent, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ChangeDetection_LocalEditAndACloudSaveThatMovedOnlyTheVersion_ReportsOnlyTheLocalChange()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var connectorId = Guid.NewGuid();
            await SyncConnectorAsync(synchronizer, files, workspace, "Contoso", connectorId, version: 5);
            await EditMetadataAsync(files, "Contoso", connectorId, metadata => metadata["iconbrandcolor"] = "#ff0000");

            var local = Assert.Single(await LocalConnectorChangesAsync(synchronizer, workspace));
            var remote = await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse(RemoteConnector("Contoso", connectorId, version: 6)).Object);

            Assert.Equal(ChangeType.Update, local.ChangeType);
            Assert.Empty(remote);
        }

        [Fact]
        public async Task ChangeDetection_RestoredOlderFolder_ReportsBothALocalAndARemoteUpdate()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var connectorId = Guid.NewGuid();
            await SyncConnectorAsync(synchronizer, files, workspace, "Contoso", connectorId, version: 5);
            await EditMetadataAsync(files, "Contoso", connectorId, metadata =>
            {
                metadata["versionnumber"] = 4;
                metadata["description"] = "Older description";
            });

            var local = Assert.Single(await LocalConnectorChangesAsync(synchronizer, workspace));
            var remote = Assert.Single(await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse(RemoteConnector("Contoso", connectorId, version: 5)).Object));

            Assert.Equal(ChangeType.Update, local.ChangeType);
            Assert.Equal(ChangeType.Update, remote.ChangeType);
        }

        [Fact]
        public async Task ChangeDetection_DeletedInTheCloud_ReportsARemoteDeleteForEveryCachedFile()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var connectorId = Guid.NewGuid();
            await SyncConnectorAsync(synchronizer, files, workspace, "Contoso", connectorId, version: 5);

            var remote = await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse().Object);

            Assert.Equal(new[] { "metadata.yml", "connectionparameters.json", "policytemplateinstances.json" }, remote.Select(change => Path.GetFileName(change.Uri)));
            Assert.All(remote, change => Assert.Equal(ChangeType.Delete, change.ChangeType));
            Assert.All(remote, change => Assert.Null(change.RemoteContent));
        }

        [Fact]
        public async Task ChangeDetection_NewLocalFolderForAConnectorTheCloudHas_ReportsACreateOnBothSides()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var connectorId = Guid.NewGuid();
            await WriteConnectorAsync(files, "Contoso", connectorId);

            var local = Assert.Single(await LocalConnectorChangesAsync(synchronizer, workspace));
            var remote = await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse(RemoteConnector("Contoso", connectorId, version: 3)).Object);

            Assert.Equal(ChangeType.Create, local.ChangeType);
            Assert.All(remote, change => Assert.Equal(ChangeType.Create, change.ChangeType));
            Assert.Contains(remote, change => change.SchemaName == local.SchemaName);
        }

        [Fact]
        public async Task ChangeDetection_NewLocalFolderNotInTheCloud_ReportsOnlyALocalCreate()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            await WriteConnectorAsync(files, "Contoso", Guid.NewGuid());

            Assert.Equal(ChangeType.Create, Assert.Single(await LocalConnectorChangesAsync(synchronizer, workspace)).ChangeType);
            Assert.Empty(await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse().Object));
        }

        [Fact]
        public async Task ChangeDetection_OneOfTwoConnectorsChangedInTheCloud_DownloadsOnlyThatConnector()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var changedId = Guid.NewGuid();
            var unchangedId = Guid.NewGuid();
            await SyncConnectorAsync(synchronizer, files, workspace, "Changed", changedId, version: 5);
            await SyncConnectorAsync(synchronizer, files, workspace, "Unchanged", unchangedId, version: 5);
            var changed = RemoteConnector("Changed", changedId, version: 6);
            changed.Description = "Edited in the portal";
            var dataverse = CreateRemoteDataverse(changed, RemoteConnector("Unchanged", unchangedId, version: 5));

            var remote = Assert.Single(await RemoteConnectorChangesAsync(synchronizer, workspace, dataverse.Object));

            Assert.Equal($"Mcs.Connector.{changedId}", remote.SchemaName);
            dataverse.Verify(d => d.DownloadConnectorsByInternalIdsAsync(It.Is<IEnumerable<string>>(internalIds => internalIds.SequenceEqual(new[] { "Changed-internal" })), true, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ChangeDetection_NoConnectorFolders_DoesNotQueryConnectors()
        {
            var (synchronizer, _, workspace) = CreateAgentWorkspace();
            var dataverse = CreateRemoteDataverse();

            Assert.Empty(await RemoteConnectorChangesAsync(synchronizer, workspace, dataverse.Object));

            dataverse.Verify(d => d.GetConnectorVersionsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ChangeDetection_OpenApiChangedInTheCloud_ReportsTheOpenApiFileWithTheServerDefinition()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var connectorId = Guid.NewGuid();
            await SyncConnectorAsync(synchronizer, files, workspace, "Contoso", connectorId, version: 5, openApiDefinition: "{\"swagger\":\"2.0\"}");
            var portalCopy = RemoteConnector("Contoso", connectorId, version: 6);
            portalCopy.OpenApiDefinition = "{\"swagger\":\"2.0\",\"host\":\"portal.example.com\"}";

            var remote = await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse(portalCopy).Object);

            Assert.Equal(new[] { "metadata.yml", "openapidefinition.json" }, remote.Select(change => Path.GetFileName(change.Uri)));
            var openApi = remote[1];
            Assert.Equal($"Mcs.Connector.{connectorId}.openapidefinition", openApi.SchemaName);
            Assert.Contains("portal.example.com", openApi.RemoteContent, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ChangeDetection_LocalOpenApiEditAndPortalDescriptionEdit_ReportDifferentFilesOfTheSameConnector()
        {
            var (synchronizer, files, workspace) = CreateAgentWorkspace();
            var connectorId = Guid.NewGuid();
            await SyncConnectorAsync(synchronizer, files, workspace, "Contoso", connectorId, version: 5, openApiDefinition: "{\"swagger\":\"2.0\"}");
            await files.WriteAsync(new AgentFilePath($"connectors/Contoso-{connectorId}/openapidefinition.json"), "{\"swagger\":\"2.0\",\"host\":\"local.example.com\"}", CancellationToken.None);
            var portalCopy = RemoteConnector("Contoso", connectorId, version: 6);
            portalCopy.OpenApiDefinition = "{\"swagger\":\"2.0\"}";
            portalCopy.Description = "Edited in the portal";

            var local = Assert.Single(await LocalConnectorChangesAsync(synchronizer, workspace));
            var remote = Assert.Single(await RemoteConnectorChangesAsync(synchronizer, workspace, CreateRemoteDataverse(portalCopy).Object));

            Assert.Equal($"Mcs.Connector.{connectorId}.openapidefinition", local.SchemaName);
            Assert.Equal($"Mcs.Connector.{connectorId}", remote.SchemaName);
            Assert.Equal(SyncedAssetSchemaName.GetAssetSchemaName(local.SchemaName), SyncedAssetSchemaName.GetAssetSchemaName(remote.SchemaName));
        }

        private static Mock<ISyncDataverseClient> CreateStoringDataverse(Action<CustomConnectorMetadata>? onSave = null, IReadOnlyCollection<Guid>? existingIds = null, long serverVersion = 1)
        {
            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.GetConnectorVersionsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((existingIds ?? Array.Empty<Guid>()).Select(id => new CustomConnectorMetadata { ConnectorId = id, VersionNumber = serverVersion }).ToArray());
            dataverse
                .Setup(d => d.SaveConnectorAsync(It.IsAny<CustomConnectorMetadata>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((CustomConnectorMetadata sent, bool _, CancellationToken _) =>
                {
                    onSave?.Invoke(sent);
                    sent.VersionNumber = serverVersion;
                    return sent;
                });
            return dataverse;
        }

        private static Mock<ISyncDataverseClient> CreateRemoteDataverse(params CustomConnectorMetadata[] remoteConnectors)
        {
            var dataverse = new Mock<ISyncDataverseClient>();
            dataverse
                .Setup(d => d.GetConnectorVersionsByIdsAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(remoteConnectors);
            dataverse
                .Setup(d => d.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), true, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IEnumerable<string> internalIds, bool _, CancellationToken _) => remoteConnectors.Where(connector => internalIds.Contains(connector.ConnectorInternalId)).ToArray());
            return dataverse;
        }

        private static CustomConnectorMetadata RemoteConnector(string name, Guid connectorId, long version) =>
            new() { ConnectorId = connectorId, Name = name, DisplayName = name, ConnectorInternalId = $"{name}-internal", VersionNumber = version };

        private static (WorkspaceSynchronizer Synchronizer, IFileAccessor Files, DirectoryPath Workspace) CreateAgentWorkspace()
        {
            var (synchronizer, fileAccessorFactory, island) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
            var workspace = new DirectoryPath($"c:/test/connectors-{Guid.NewGuid():N}/");
            var files = fileAccessorFactory.Create(workspace);
            WorkspaceSynchronizer.WriteCloudCache(files, ComponentWriterDefensiveTests.CreateAgentDefinition());
            island
                .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PvaComponentChangeSet(null, null, "token-1"));
            return (synchronizer, files, workspace);
        }

        private static async Task WriteConnectorAsync(IFileAccessor files, string name, Guid connectorId, string? openApiDefinition = null)
        {
            var folder = $"connectors/{name}-{connectorId}";
            var metadata = new Dictionary<string, object?> { ["connectorid"] = connectorId, ["name"] = name, ["displayname"] = name, ["connectorinternalid"] = $"{name}-internal" };
            if (openApiDefinition != null)
            {
                metadata["openapidefinition"] = $"{folder}/openapidefinition.json";
                await files.WriteAsync(new AgentFilePath($"{folder}/openapidefinition.json"), openApiDefinition, CancellationToken.None);
            }

            await files.WriteAsync(new AgentFilePath($"{folder}/metadata.yml"), JsonSerializer.Serialize(metadata), CancellationToken.None);
        }

        private static async Task SyncConnectorAsync(WorkspaceSynchronizer synchronizer, IFileAccessor files, DirectoryPath workspace, string name, Guid connectorId, long version, string? openApiDefinition = null)
        {
            await WriteConnectorAsync(files, name, connectorId, openApiDefinition);
            await synchronizer.PushCustomConnectorsAsync(workspace, CreateStoringDataverse(serverVersion: version).Object, CancellationToken.None);
        }

        private static async Task EditMetadataAsync(IFileAccessor files, string name, Guid connectorId, Action<JsonObject> edit)
        {
            var path = new AgentFilePath($"connectors/{name}-{connectorId}/metadata.yml");
            var metadata = (JsonNode.Parse(await files.ReadStringAsync(path, CancellationToken.None)) ?? throw new Xunit.Sdk.XunitException("Expected a non-null test value.")).AsObject();
            edit(metadata);
            await files.WriteAsync(path, metadata.ToJsonString(), CancellationToken.None);
        }

        private static async Task<IReadOnlyList<Change>> LocalConnectorChangesAsync(WorkspaceSynchronizer synchronizer, DirectoryPath workspace)
        {
            var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, ComponentWriterDefensiveTests.CreateAgentDefinition(), CancellationToken.None);
            return changes.Where(change => change.ChangeKind == CustomConnectorLayout.ChangeKind).ToList();
        }

        private static async Task<IReadOnlyList<Change>> RemoteConnectorChangesAsync(WorkspaceSynchronizer synchronizer, DirectoryPath workspace, ISyncDataverseClient dataverse)
        {
            var (_, changes) = await synchronizer.GetRemoteChangesAsync(workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(), dataverse, new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);
            return changes.Where(change => change.ChangeKind == CustomConnectorLayout.ChangeKind).ToList();
        }

        private DirectoryPath NewWorkspace()
        {
            var path = Path.Combine(_tempRoot, "ws-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new DirectoryPath(path.Replace('\\', '/') + "/");
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