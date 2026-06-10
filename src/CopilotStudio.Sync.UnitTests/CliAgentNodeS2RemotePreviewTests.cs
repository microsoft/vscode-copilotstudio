// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Node S2 (TDD D32) - CLI connection-reference REMOTE-change preview must diff the
// cloud-applied snapshot against the cloud cache (cloud-vs-cloud), NOT local disk, and
// the emitted Change.Uri must use the .sync.yaml extension (D28).
//
// These lock the two review findings:
//   2. GetCliConnectionReferenceChanges read local disk (ListDiskLogicalNames)
//      unconditionally, so on a remote (pull) preview a local-only delete showed up as an
//      incoming remote delete and a real cloud-side delete was missed.
//   A. The connection Change.Uri was hardcoded ".yaml"; the writer emits ".sync.yaml".
//
// The remote-preview call shape mirrors GetRemoteChangesAsync:
//   GetLocalChanges(appliedDefinition /* new cloud */, cloudSnapshot /* old cloud */,
//                   fileAccessor, token, isRemoteChange: true).

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentNodeS2RemotePreviewTests
{
    private static string CrUri(string logicalName) =>
        $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{logicalName}{CliAgentConnectionsWriter.FileExtension}";

    private static AgentFilePath CrPath(string logicalName) => new AgentFilePath(CrUri(logicalName));

    private static ConnectionReference MakeConnectionRef(string logicalName, string connectorId) =>
        new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = logicalName,
            ConnectorId = connectorId,
        }.Build();

    [Fact]
    public async Task RemotePreview_CloudDeletedConnectionRef_DetectedAsRemoteDelete_EvenWhenStillOnDisk()
    {
        var (_, _, accessor, synchronizer, _) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;

        var target = cloud.ConnectionReferences.First(cr => cr.Id.Value != Guid.Empty);
        var targetName = target.ConnectionReferenceLogicalName.Value!;

        // The cloud DELETED this ref: the applied (new cloud) state omits it.
        var appliedRefs = cloud.ConnectionReferences
            .Where(cr => !cr.ConnectionReferenceLogicalName.Equals(target.ConnectionReferenceLogicalName))
            .ToList();
        var applied = ((BotDefinition)cloud).WithConnectionReferences(appliedRefs);

        // The disk file is STILL present - proving the remote diff does not consult disk.
        Assert.True(accessor.Exists(CrPath(targetName)),
            "Sanity: the CR file should still be on disk for this scenario.");

        var (_, changes) = synchronizer.GetLocalChanges(applied, cloud, accessor, "token-1", isRemoteChange: true);

        var delete = Assert.Single(changes, c =>
            c.ChangeKind == nameof(ConnectionReference)
            && c.ChangeType == ChangeType.Delete
            && c.Name == targetName);
        Assert.Equal(CrUri(targetName), delete.Uri);
    }

    [Fact]
    public async Task RemotePreview_LocalOnlyDiskDeletion_NotShownAsRemoteDelete()
    {
        var (_, _, accessor, synchronizer, _) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;

        // No remote change: the applied (new cloud) state equals the cloud cache.
        var applied = cloud;

        // A LOCAL-only edit: the user deleted a CR file on disk.
        var target = cloud.ConnectionReferences.First(cr => cr.Id.Value != Guid.Empty);
        accessor.Delete(CrPath(target.ConnectionReferenceLogicalName.Value!));

        var (_, changes) = synchronizer.GetLocalChanges(applied, cloud, accessor, "token-1", isRemoteChange: true);

        // The local-only delete must NOT surface as an incoming remote delete.
        Assert.DoesNotContain(changes, c =>
            c.ChangeKind == nameof(ConnectionReference) && c.ChangeType == ChangeType.Delete);
    }

    [Fact]
    public async Task RemotePreview_CloudInsertedConnectionRef_EmitsCreate_WithSyncYamlUri()
    {
        var (_, _, accessor, synchronizer, _) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;

        const string newName = "remote_added_cr";
        var appliedRefs = cloud.ConnectionReferences
            .Append(MakeConnectionRef(newName, "/providers/Microsoft.PowerApps/apis/shared_remoteadded"))
            .ToList();
        var applied = ((BotDefinition)cloud).WithConnectionReferences(appliedRefs);

        var (_, changes) = synchronizer.GetLocalChanges(applied, cloud, accessor, "token-1", isRemoteChange: true);

        var create = Assert.Single(changes, c =>
            c.ChangeKind == nameof(ConnectionReference)
            && c.ChangeType == ChangeType.Create
            && c.Name == newName);
        Assert.Equal(CrUri(newName), create.Uri);
    }

    [Fact]
    public async Task RemotePreview_CloudChangedConnectorId_EmitsUpdate_WithSyncYamlUri()
    {
        var (_, _, accessor, synchronizer, _) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;

        var target = cloud.ConnectionReferences.First(cr => cr.Id.Value != Guid.Empty);
        var targetName = target.ConnectionReferenceLogicalName.Value!;

        var changed = target.ToBuilder();
        changed.ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_remotechanged";
        var appliedRefs = cloud.ConnectionReferences
            .Where(cr => !cr.ConnectionReferenceLogicalName.Equals(target.ConnectionReferenceLogicalName))
            .Append(changed.Build())
            .ToList();
        var applied = ((BotDefinition)cloud).WithConnectionReferences(appliedRefs);

        var (_, changes) = synchronizer.GetLocalChanges(applied, cloud, accessor, "token-1", isRemoteChange: true);

        var update = Assert.Single(changes, c =>
            c.ChangeKind == nameof(ConnectionReference)
            && c.ChangeType == ChangeType.Update
            && c.Name == targetName);
        Assert.Equal(CrUri(targetName), update.Uri);
    }

    [Fact]
    public async Task LocalPush_NewConnectionRefFile_ChangeUri_UsesSyncYamlExtension()
    {
        // The .sync.yaml URI fix (D28) applies to the local (push) path too.
        var (_, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        const string newName = "user_added_cr";
        await accessor.WriteAsync(CrPath(newName),
            "connectionReferences:\n" +
            $"  - connectionReferenceLogicalName: {newName}\n" +
            "    connectorId: /providers/Microsoft.PowerApps/apis/shared_userconnector\n",
            CancellationToken.None);

        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        var create = Assert.Single(changes, c =>
            c.ChangeKind == nameof(ConnectionReference)
            && c.ChangeType == ChangeType.Create
            && c.Name == newName);
        Assert.Equal(CrUri(newName), create.Uri);
        Assert.EndsWith(".sync.yaml", create.Uri);
    }
}
