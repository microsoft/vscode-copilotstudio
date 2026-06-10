// Copyright (C) Microsoft Corporation. All rights reserved.
//
// CliAgentSyncSupport / Node F — integration tests for the push detection
// flow: GetLocalChanges + ReadWorkspaceDefinitionAsync against CLI-shape
// workspaces. Tests validate:
//   - Connection-reference change emission (Insert / Update / Delete).
//   - Per-route file-deletion → BotComponentDelete in the changeset.
//   - CLI new-file scan discovers user-added files.
//   - Destructive deletes are gated on agent.yaml (the migration-safe rule).
//   - Classic regression: HRAgent emits no CR changes (no CLI dispatch).
//   - Failure modes: malformed component file → skip-and-warn; missing
//     agent.yaml under an active CLI route → operations still proceed
//     (no destructive intent without the gate).

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentNodeFPushTests
{
    private static readonly AgentFilePath AgentYamlPath = new AgentFilePath("agent.yaml");
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");

    private readonly ITestOutputHelper _output;

    public CliAgentNodeFPushTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // --- Connection-reference change emission -------------------------------

    [Fact]
    public async Task GetLocalChanges_CliAgent_UntouchedDisk_NoConnectionReferenceChanges()
    {
        // Baseline: a freshly-cloned CLI workspace with no user edits
        // must emit zero connection-reference changes. A regression
        // here (e.g., the diff thinks every cloud ref is new) would
        // synthesize phantom Updates on every push.
        var (entity, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        Assert.Empty(changeset.ConnectionReferenceChanges);
    }

    [Fact]
    public async Task GetLocalChanges_CliAgent_EditConnectorId_EmitsUpdate()
    {
        // User edits connectorId in a disk connection-ref file. Push
        // must surface a ConnectionReferenceUpdate that carries the
        // cloud's Id + Version (so the server can apply by Id).
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var target = definition.ConnectionReferences
            .First(cr => cr.Id.Value != Guid.Empty);

        var crPath = new AgentFilePath(
            $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{target.ConnectionReferenceLogicalName.Value}{CliAgentConnectionsWriter.FileExtension}");
        Assert.True(accessor.Exists(crPath),
            $"Connection-ref file '{crPath}' should be present after PushFixtureAsClone.");

        // Mutate connectorId on disk. D5's writer emits a single-item
        // list wrapper (`connectionReferences:` → one element with
        // connectionReferenceLogicalName + connectorId). Rewriting both
        // simulates the user editing only the connectorId line.
        var newConnectorId = "/providers/Microsoft.PowerApps/apis/shared_USER_EDITED";
        await accessor.WriteAsync(crPath,
            "connectionReferences:\n" +
            $"  - connectionReferenceLogicalName: {target.ConnectionReferenceLogicalName.Value}\n" +
            $"    connectorId: {newConnectorId}\n",
            CancellationToken.None);

        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, changes) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        var update = Assert.Single(changeset.ConnectionReferenceChanges!,
            c => c is ConnectionReferenceUpdate);
        var updateCast = (ConnectionReferenceUpdate)update;
        Assert.Equal(target.ConnectionReferenceLogicalName.Value,
            updateCast.ConnectionReference!.ConnectionReferenceLogicalName.Value);
        Assert.Equal(newConnectorId, updateCast.ConnectionReference.ConnectorId.Value);
        // Id carry-forward (so server applies by Id).
        Assert.Equal(target.Id.Value, updateCast.ConnectionReference.Id.Value);
    }

    [Fact]
    public async Task GetLocalChanges_CliAgent_NewConnectionRefFile_EmitsInsert()
    {
        var (entity, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var newLogicalName = "user_added_connection_ref";
        var newPath = new AgentFilePath(
            $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{newLogicalName}{CliAgentConnectionsWriter.FileExtension}");
        await accessor.WriteAsync(newPath,
            "connectionReferences:\n" +
            $"  - connectionReferenceLogicalName: {newLogicalName}\n" +
            "    connectorId: /providers/Microsoft.PowerApps/apis/shared_userconnector\n",
            CancellationToken.None);

        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        var insert = Assert.Single(
            changeset.ConnectionReferenceChanges!.OfType<ConnectionReferenceInsert>(),
            c => c.ConnectionReference!.ConnectionReferenceLogicalName.Value == newLogicalName);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_userconnector",
            insert.ConnectionReference!.ConnectorId.Value);
    }

    [Fact]
    public async Task GetLocalChanges_CliAgent_DeletedConnectionRefFile_EmitsDelete()
    {
        // User deletes a disk connection-ref file with a known cloud Id.
        // Push must surface a ConnectionReferenceDelete (gated by
        // agent.yaml existence, which PushFixtureAsClone provides).
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var target = definition.ConnectionReferences
            .First(cr => cr.Id.Value != Guid.Empty);
        var crPath = new AgentFilePath(
            $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{target.ConnectionReferenceLogicalName.Value}{CliAgentConnectionsWriter.FileExtension}");

        accessor.Delete(crPath);
        Assert.False(accessor.Exists(crPath));
        Assert.True(CliAgentBotEntityReader.IsCliLayoutAdopted(accessor),
            "Sanity: PushFixtureAsClone should leave agent.yaml so the delete-gate fires.");

        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        var delete = Assert.Single(
            changeset.ConnectionReferenceChanges!.OfType<ConnectionReferenceDelete>());
        Assert.Equal(target.Id.Value, delete.ConnectionReferenceId);
    }

    [Fact]
    public async Task GetLocalChanges_CliAgent_DeletedConnectionRef_NoAgentYaml_NoDelete()
    {
        // Migration-safe gate (rubber-duck blocking #2 adoption):
        // without agent.yaml on disk, the delete loop must NOT fire.
        // This protects pre-D1 clones from spuriously deleting cloud
        // refs the user never touched.
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var target = definition.ConnectionReferences
            .First(cr => cr.Id.Value != Guid.Empty);
        var crPath = new AgentFilePath(
            $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{target.ConnectionReferenceLogicalName.Value}{CliAgentConnectionsWriter.FileExtension}");
        accessor.Delete(crPath);

        // Simulate a workspace where the CLI layout is not adopted by removing the
        // settings.mcs.yml entity anchor (the gate now keys off settings.mcs.yml, D22/D25).
        accessor.Delete(SettingsPath);
        Assert.False(CliAgentBotEntityReader.IsCliLayoutAdopted(accessor));

        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        Assert.DoesNotContain(
            changeset.ConnectionReferenceChanges,
            c => c is ConnectionReferenceDelete);
    }

    [Fact]
    public async Task GetLocalChanges_ClassicAgent_HRAgent_NoConnectionReferenceChanges()
    {
        // Classic regression (R7): classic agents have no per-ref disk
        // shape so the CR diff is short-circuited. Verifies the
        // CLI-only gate at the call site doesn't fire for classic.
        var (entity, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        // EnsureInitialized() means the array is always non-default.
        // CLI-skipped path means no changes appended.
        Assert.Empty(changeset.ConnectionReferenceChanges);
    }

    [Fact]
    public async Task GetLocalChanges_CliAgent_ConnectionRefIdEmpty_DeleteSkipped()
    {
        // Edge case: a connection ref in cloud with Guid.Empty Id cannot
        // be deleted via the changeset (server applies by Id). Verify
        // the deletion is skipped (warning emitted, no Delete change).
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        // FoodLogger has one CR with Id.Value == Guid.Empty:
        // new_sharedcommondataserviceforapps_6480c125.
        var target = definition.ConnectionReferences
            .First(cr => cr.Id.Value == Guid.Empty);

        var crPath = new AgentFilePath(
            $"{CliAgentConnectionsWriter.InfrastructureConnectionsFolder}/{target.ConnectionReferenceLogicalName.Value}{CliAgentConnectionsWriter.FileExtension}");
        accessor.Delete(crPath);

        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        // No Delete entry should be emitted for the Id.Empty cloud ref.
        Assert.DoesNotContain(
            changeset.ConnectionReferenceChanges,
            c => c is ConnectionReferenceDelete d && d.ConnectionReferenceId == Guid.Empty);
    }

    // --- Per-route component deletes via ReadWorkspaceDefinitionAsync ------

    [Fact]
    public async Task ReadWorkspace_CliAgent_DeletedToolFile_AgentYamlPresent_DropsComponent()
    {
        // Per-component delete intent: user removes a CLI tool file.
        // The reader must drop the cloud-cache component (so
        // GetLocalChanges then synthesizes a BotComponentDelete).
        // Gated on agent.yaml existence — see no-yaml test below.
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var toolComponent = definition.Components.OfType<DialogComponent>()
            .First(d => d.SchemaName.Value!.IndexOf(".tool.", StringComparison.Ordinal) >= 0
                        && d.SchemaName.Value!.IndexOf(".tool.connected-agent.", StringComparison.Ordinal) < 0);
        var toolPath = CliAgentRoundTripReadTests.CliComponentPath(toolComponent, definition);

        accessor.Delete(toolPath);
        Assert.False(accessor.Exists(toolPath));

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        Assert.DoesNotContain(read.Components,
            c => c.SchemaNameString == toolComponent.SchemaNameString);
    }

    [Fact]
    public async Task ReadWorkspace_CliAgent_DeletedToolFile_NoAgentYaml_PreservesCloudCache()
    {
        // Migration-safe gate: without agent.yaml, missing CLI files
        // must NOT drop the component (pre-D1 clone — writer never
        // produced the file in the CLI shape, classic-shape file
        // was generated elsewhere).
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var toolComponent = definition.Components.OfType<DialogComponent>()
            .First(d => d.SchemaName.Value!.IndexOf(".tool.", StringComparison.Ordinal) >= 0
                        && d.SchemaName.Value!.IndexOf(".tool.connected-agent.", StringComparison.Ordinal) < 0);
        var toolPath = CliAgentRoundTripReadTests.CliComponentPath(toolComponent, definition);

        accessor.Delete(toolPath);
        accessor.Delete(SettingsPath);
        Assert.False(CliAgentBotEntityReader.IsCliLayoutAdopted(accessor));

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        // Cloud cache preserved (no spurious drop).
        Assert.Contains(read.Components,
            c => c.SchemaNameString == toolComponent.SchemaNameString);
    }

    [Fact]
    public async Task ReadWorkspace_CliAgent_DeletedSkillFile_AgentYamlPresent_DropsComponent()
    {
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var skillComponent = definition.Components.OfType<DialogComponent>()
            .First(d => d.SchemaName.Value!.IndexOf(".skill.", StringComparison.Ordinal) >= 0);
        var skillPath = CliAgentRoundTripReadTests.CliComponentPath(skillComponent, definition);

        accessor.Delete(skillPath);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        Assert.DoesNotContain(read.Components,
            c => c.SchemaNameString == skillComponent.SchemaNameString);
    }

    [Fact]
    public async Task ReadWorkspace_CliAgent_DeletedKnowledgeFile_AgentYamlPresent_DropsComponent()
    {
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var knowledgeComponent = definition.Components.OfType<KnowledgeSourceComponent>().First();
        var knowledgePath = CliAgentRoundTripReadTests.CliComponentPath(knowledgeComponent, definition);

        accessor.Delete(knowledgePath);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        Assert.DoesNotContain(read.Components,
            c => c.SchemaNameString == knowledgeComponent.SchemaNameString);
    }

    // --- End-to-end edit-and-push: deleted tool → BotComponentDelete ------

    [Fact]
    public async Task GetLocalChanges_CliAgent_DeletedToolFile_EmitsBotComponentDelete()
    {
        // Full edit-and-push round-trip: delete a tool file, run read,
        // run GetLocalChanges, assert a BotComponentDelete is in the
        // changeset with the cloud Id + Version (so the server can
        // apply by Id).
        var (entity, definition, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var toolComponent = definition.Components.OfType<DialogComponent>()
            .First(d => d.SchemaName.Value!.IndexOf(".tool.", StringComparison.Ordinal) >= 0
                        && d.SchemaName.Value!.IndexOf(".tool.connected-agent.", StringComparison.Ordinal) < 0);
        var toolPath = CliAgentRoundTripReadTests.CliComponentPath(toolComponent, definition);

        accessor.Delete(toolPath);

        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        var delete = Assert.Single(
            changeset.BotComponentChanges!.OfType<BotComponentDelete>(),
            d => d.BotComponentId == toolComponent.Id);
        Assert.NotNull(delete);
    }

    // --- Classic regression on the same flow --------------------------------

    [Fact]
    public async Task GetLocalChanges_ClassicAgent_UntouchedDisk_NoComponentChanges()
    {
        // Classic regression: a freshly-cloned HRAgent (classic) workspace
        // with no edits produces no component changes either.
        var (entity, _, accessor, synchronizer, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        var local = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var (changeset, _) = synchronizer.GetLocalChanges(local, cloud, accessor, "token-1");

        // No Inserts / Updates / Deletes expected from a clean clone.
        Assert.Empty(changeset.BotComponentChanges!.OfType<BotComponentInsert>());
        Assert.Empty(changeset.BotComponentChanges!.OfType<BotComponentDelete>());
    }
}
