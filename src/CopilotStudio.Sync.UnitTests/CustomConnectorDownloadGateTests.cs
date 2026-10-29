// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System.Collections.Immutable;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CustomConnectorDownloadGateTests
{
    [Fact]
    public async Task SyncWorkspace_SkipsConnectorDownload_WhenVersionUnchanged_AndRedownloadsWhenBumped()
    {
        var (synchronizer, _, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "conn-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");

        try
        {
            var connectorRowId = Guid.NewGuid();
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

            var probeVersion = 5L;
            var mockDataverse = new Mock<ISyncDataverseClient>();
            mockDataverse
                .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<AIPromptMetadata>());
            mockDataverse
                .Setup(x => x.GetConnectorVersionsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", VersionNumber = probeVersion } });
            mockDataverse
                .Setup(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", VersionNumber = probeVersion, Name = "TestConnector" } });

            var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
            var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

            await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
            mockDataverse.Verify(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);

            Directory.CreateDirectory(Path.Combine(workspaceRoot, "connectors", "TestConnector-" + connectorRowId));

            await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
            mockDataverse.Verify(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);

            probeVersion = 6L;
            await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
            mockDataverse.Verify(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, true);
            }
        }
    }

    [Fact]
    public async Task SyncWorkspace_DoesNotAdvanceDownloadBaseline_WhenScheduledConnectorIsNotReturned()
    {
        var (synchronizer, _, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "conn-gate-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");

        try
        {
            var connectorRowId = Guid.NewGuid();
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

            var probeVersion = 5L;
            var downloadReturnsEmpty = false;
            var mockDataverse = new Mock<ISyncDataverseClient>();
            mockDataverse
                .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(Array.Empty<AIPromptMetadata>());
            mockDataverse
                .Setup(x => x.GetConnectorVersionsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", VersionNumber = probeVersion } });
            mockDataverse
                .Setup(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => downloadReturnsEmpty
                    ? Array.Empty<CustomConnectorMetadata>()
                    : new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", VersionNumber = probeVersion, Name = "TestConnector" } });

            var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
            var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

            await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
            mockDataverse.Verify(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Once);

            Directory.CreateDirectory(Path.Combine(workspaceRoot, "connectors", "TestConnector-" + connectorRowId));

            probeVersion = 6L;
            downloadReturnsEmpty = true;
            await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
            mockDataverse.Verify(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Exactly(2));

            downloadReturnsEmpty = false;
            await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
            mockDataverse.Verify(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Exactly(3));
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, true);
            }
        }
    }

    [Fact]
    public async Task SyncWorkspace_VersionBumpWithSameContent_RewritesOnlyMetadataAndReportsNoLocalChange()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/conn-gate-stable-" + Guid.NewGuid().ToString("N") + "/");
        var files = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        var connectorRowId = Guid.NewGuid();
        var cloudFlowMetadata = new CloudFlowMetadata
        {
            Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
            ConnectionReferences = ImmutableArray.Create(new ConnectionReference(
                connectionReferenceLogicalName: "cr1.shared_test." + Guid.NewGuid().ToString("N"),
                connectionId: string.Empty,
                connectorId: "/providers/Microsoft.PowerApps/apis/shared_test")),
        };

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!;
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, botEntity, "token-1"));

        var probeVersion = 5L;
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.GetConnectorVersionsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", VersionNumber = probeVersion } });
        mockDataverse
            .Setup(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[]
            {
                new CustomConnectorMetadata
                {
                    ConnectorId = connectorRowId,
                    ConnectorInternalId = "shared_test",
                    Name = "TestConnector",
                    OpenApiDefinition = "{\"swagger\":\"2.0\"}",
                    IconBlobBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                    VersionNumber = probeVersion,
                },
            });

        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);

        var connectorFolder = $"connectors/TestConnector-{connectorRowId}";
        var untouched = new[] { "openapidefinition.json", "connectionparameters.json", "policytemplateinstances.json", "iconblob.png" }.ToDictionary(fileName => $"{connectorFolder}/{fileName}", fileName => files.Files[$"{connectorFolder}/{fileName}"]);

        probeVersion = 6L;
        await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
        var (_, localChanges) = await synchronizer.GetLocalChangesAsync(workspace, new BotDefinition().WithEntity(botEntity), CancellationToken.None);

        Assert.All(untouched, file => Assert.Same(file.Value, files.Files[file.Key]));
        Assert.Contains("\"versionnumber\": 6", await files.ReadStringAsync(new AgentFilePath($"{connectorFolder}/metadata.yml"), CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("\"6\"", await files.ReadStringAsync(new AgentFilePath(".mcs/.connectors-download.json"), CancellationToken.None), StringComparison.Ordinal);
        Assert.DoesNotContain(localChanges, change => change.ChangeKind == CustomConnectorLayout.ChangeKind);
    }

    [Fact]
    public async Task SyncWorkspace_UnchangedConnectorWithoutCloudCopy_SeedsTheCloudCopyFromTheServerAndKeepsLocalEdits()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/conn-gate-seed-" + Guid.NewGuid().ToString("N") + "/");
        var files = fileAccessorFactory.Create(workspace);
        var connectorRowId = Guid.NewGuid();
        var cloudFlowMetadata = new CloudFlowMetadata
        {
            Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
            ConnectionReferences = ImmutableArray.Create(new ConnectionReference(
                connectionReferenceLogicalName: "cr1.shared_test." + Guid.NewGuid().ToString("N"),
                connectionId: string.Empty,
                connectorId: "/providers/Microsoft.PowerApps/apis/shared_test")),
        };

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!;
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, botEntity, "token-1"));

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.GetConnectorVersionsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", VersionNumber = 5L } });
        mockDataverse
            .Setup(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", Name = "TestConnector", OpenApiDefinition = "{\"swagger\":\"2.0\"}", VersionNumber = 5L } });

        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);

        var openApiPath = new AgentFilePath($"connectors/TestConnector-{connectorRowId}/openapidefinition.json");
        var localOpenApi = "{\"swagger\":\"2.0\",\"host\":\"local.example.com\"}";
        await files.WriteAsync(openApiPath, localOpenApi, CancellationToken.None);
        files.Delete(SyncedAssetKind.CustomConnector.CloudCopyPath);

        await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
        var (_, localChanges) = await synchronizer.GetLocalChangesAsync(workspace, new BotDefinition().WithEntity(botEntity), CancellationToken.None);

        mockDataverse.Verify(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(localOpenApi, await files.ReadStringAsync(openApiPath, CancellationToken.None));
        Assert.DoesNotContain("local.example.com", SyncedAssetKind.ReadCloudCopy(files, $"Mcs.Connector.{connectorRowId}.openapidefinition"), StringComparison.Ordinal);
        Assert.Equal($"Mcs.Connector.{connectorRowId}.openapidefinition", Assert.Single(localChanges, change => change.ChangeKind == CustomConnectorLayout.ChangeKind).SchemaName);
    }

    [Fact]
    public async Task SyncWorkspace_RestoredOlderConnectorFolder_IsReplacedByTheCloudCopy()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/conn-gate-restored-" + Guid.NewGuid().ToString("N") + "/");
        var files = fileAccessorFactory.Create(workspace);
        var connectorRowId = Guid.NewGuid();
        var cloudFlowMetadata = new CloudFlowMetadata
        {
            Workflows = ImmutableArray<CloudFlowDefinition>.Empty,
            ConnectionReferences = ImmutableArray.Create(new ConnectionReference(
                connectionReferenceLogicalName: "cr1.shared_test." + Guid.NewGuid().ToString("N"),
                connectionId: string.Empty,
                connectorId: "/providers/Microsoft.PowerApps/apis/shared_test")),
        };

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: cr1")!;
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(null, botEntity, "token-1"));

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.GetConnectorVersionsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", VersionNumber = 5L } });
        mockDataverse
            .Setup(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = connectorRowId, ConnectorInternalId = "shared_test", Name = "TestConnector", OpenApiDefinition = "{\"host\":\"current.example.com\"}", VersionNumber = 5L } });

        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);

        var metadataPath = new AgentFilePath($"connectors/TestConnector-{connectorRowId}/metadata.yml");
        var openApiPath = new AgentFilePath($"connectors/TestConnector-{connectorRowId}/openapidefinition.json");
        await files.WriteAsync(metadataPath, (await files.ReadStringAsync(metadataPath, CancellationToken.None)).Replace("\"versionnumber\": 5", "\"versionnumber\": 4"), CancellationToken.None);
        await files.WriteAsync(openApiPath, "{\"host\":\"restored.example.com\"}", CancellationToken.None);

        await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);
        var (_, localChanges) = await synchronizer.GetLocalChangesAsync(workspace, new BotDefinition().WithEntity(botEntity), CancellationToken.None);

        mockDataverse.Verify(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Contains("current.example.com", await files.ReadStringAsync(openApiPath, CancellationToken.None), StringComparison.Ordinal);
        Assert.Contains("\"versionnumber\": 5", await files.ReadStringAsync(metadataPath, CancellationToken.None), StringComparison.Ordinal);
        Assert.DoesNotContain(localChanges, change => change.ChangeKind == CustomConnectorLayout.ChangeKind);
    }

    [Fact]
    public async Task SyncWorkspace_PrunesStaleConnectorFolder_WhenProbeFailsButDownloadSucceeds()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/conn-gate-probefail-" + Guid.NewGuid().ToString("N") + "/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var currentRowId = Guid.NewGuid();
        var staleRowId = Guid.NewGuid();
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

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        mockDataverse
            .Setup(x => x.GetConnectorVersionsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe failed"));
        mockDataverse
            .Setup(x => x.DownloadConnectorsByInternalIdsAsync(It.IsAny<IEnumerable<string>>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { new CustomConnectorMetadata { ConnectorId = currentRowId, ConnectorInternalId = "shared_test", VersionNumber = 5L, Name = "TestConnector" } });

        var stalePath = new AgentFilePath($"connectors/OldConnector-{staleRowId}/metadata.yml");
        await fileAccessor.WriteAsync(stalePath, $"connectorId: {staleRowId}\nname: OldConnector", CancellationToken.None);

        var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

        await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);

        Assert.False(fileAccessor.Exists(stalePath), "stale connector folder should be pruned after a successful fallback download");
        Assert.True(
            fileAccessor.ListFiles("connectors").Any(file => file.ToString().Contains(currentRowId.ToString())),
            "the current connector should still be downloaded into the workspace");
    }
}
