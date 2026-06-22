// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Review follow-up (Node S4H, TDD D38) - the VS Code push / local-diff path passes
// workspace.Definition (the MCS compiler output) into GetLocalChangesAsync. CLI connection
// references live in infrastructure/connections/*.sync.yaml, which route to generic YAML and
// never reach the MCS compiler, so that definition does NOT contain them. GetLocalChangesAsync
// must overlay the on-disk CLI connection references before diffing, or CLI connection-reference
// CREATE/UPDATE are missed on the VS Code path (the S2 test used ReadWorkspaceDefinitionAsync,
// which already overlays, so it did not prove this path).

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentNodeS4HIntegrationTests
{
    private static string CrUri(string logicalName) =>
        $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{logicalName}{CliAgentConnectionsWriter.FileExtension}";

    private static AgentFilePath CrPath(string logicalName) => new AgentFilePath(CrUri(logicalName));

    private static Task WriteCrFileAsync(InMemoryFileAccessor accessor, string logicalName, string connectorId) =>
        accessor.WriteAsync(CrPath(logicalName),
            "connectionReferences:\n" +
            $"  - connectionReferenceLogicalName: {logicalName}\n" +
            $"    connectorId: {connectorId}\n",
            CancellationToken.None);

    [Fact]
    public async Task GetLocalChangesAsync_CliNewConnectionRefOnDisk_DetectedAsCreate_WhenDefinitionLacksIt()
    {
        var (_, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloudSnapshot = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;

        const string newName = "user_added_cr";
        await WriteCrFileAsync(accessor, newName, "/providers/Microsoft.PowerApps/apis/shared_userconnector");

        // Pass the cloud snapshot as the workspace definition: this models the LSP
        // workspace.Definition (MCS compiler output), which does NOT include the .sync.yaml
        // overlay refs. Without the GetLocalChangesAsync overlay the create would be missed.
        var (_, changes) = await synchronizer.GetLocalChangesAsync(
            workspace, cloudSnapshot, new Mock<ISyncDataverseClient>().Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        var create = Assert.Single(changes, c =>
            c.ChangeKind == nameof(ConnectionReference)
            && c.ChangeType == ChangeType.Create
            && c.Name == newName);
        Assert.Equal(CrUri(newName), create.Uri);
    }

    [Fact]
    public async Task GetLocalChangesAsync_CliEditedConnectorIdOnDisk_DetectedAsUpdate_WhenDefinitionLacksIt()
    {
        var (_, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloudSnapshot = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;

        var target = cloudSnapshot.ConnectionReferences.First(cr => cr.Id.Value != Guid.Empty);
        var targetName = target.ConnectionReferenceLogicalName.Value!;

        // Edit the connectorId on disk (overwrite the existing per-file).
        await WriteCrFileAsync(accessor, targetName, "/providers/Microsoft.PowerApps/apis/shared_EDITED");

        var (_, changes) = await synchronizer.GetLocalChangesAsync(
            workspace, cloudSnapshot, new Mock<ISyncDataverseClient>().Object,
            new AgentSyncInfo { AgentId = Guid.NewGuid() }, CancellationToken.None);

        Assert.Contains(changes, c =>
            c.ChangeKind == nameof(ConnectionReference)
            && c.ChangeType == ChangeType.Update
            && c.Name == targetName);
    }

    [Fact]
    public async Task ProvisionConnectionReferencesAsync_RawDefinitionPlusDiskOnlyCliRef_ProvisionsTheDiskRef()
    {
        // The VS Code push/reattach handlers call ProvisionConnectionReferencesAsync with the
        // raw workspace.Definition (MCS compiler output, no .sync.yaml overlay). The
        // folder-aware overload must overlay the on-disk CLI connection references first, or a
        // disk-only ref is detected for push but never provisioned (EnsureConnectionReferenceExistsAsync
        // is never called for it). TDD D38 review follow-up.
        var (_, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloudSnapshot = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;

        const string newName = "user_added_cr";
        const string newConnector = "/providers/Microsoft.PowerApps/apis/shared_userconnector";
        await WriteCrFileAsync(accessor, newName, newConnector);

        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.EnsureConnectionReferenceExistsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<Guid?>()))
            .Returns(Task.CompletedTask);

        // Folder-aware overload: overlays the disk refs onto the raw definition before provisioning.
        await synchronizer.ProvisionConnectionReferencesAsync(
            workspace, cloudSnapshot, mockDataverse.Object, CancellationToken.None);

        mockDataverse.Verify(x => x.EnsureConnectionReferenceExistsAsync(
            newName, newConnector, It.IsAny<CancellationToken>(), It.IsAny<Guid?>()), Times.Once);
    }
}
