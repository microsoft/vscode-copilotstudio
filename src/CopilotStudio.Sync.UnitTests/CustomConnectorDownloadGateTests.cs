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
    public async Task SyncWorkspace_PrunesStaleConnectorFolder_WhenProbeFailsButDownloadSucceeds()
    {
        var (synchronizer, _, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "conn-gate-probefail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        var workspace = new DirectoryPath(workspaceRoot.Replace('\\', '/') + "/");

        try
        {
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

            var staleFolder = Path.Combine(workspaceRoot, "connectors", "OldConnector-" + staleRowId);
            Directory.CreateDirectory(staleFolder);

            var opContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
            var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };

            await synchronizer.SyncWorkspaceAsync(workspace, opContext, null, false, mockDataverse.Object, syncInfo, cloudFlowMetadata, CancellationToken.None);

            Assert.False(Directory.Exists(staleFolder), "stale connector folder should be pruned after a successful fallback download");
        }
        finally
        {
            if (Directory.Exists(workspaceRoot))
            {
                Directory.Delete(workspaceRoot, true);
            }
        }
    }
}
