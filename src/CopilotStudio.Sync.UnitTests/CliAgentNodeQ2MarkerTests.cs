// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Node Q2 (TDD D29) - forward-looking workspace layout marker `agent.sync.yaml`.
// Validates:
//   - CLI clone emits `agent.sync.yaml { layoutVersion: 1 }`; classic emits zero .sync.*.
//   - Marker-aware DetectWorkspaceLayout: present+known is authoritative for the LAYOUT
//     axis but NEVER shape-authoritative (mismatch fails closed); absent -> content
//     inference (transition fallback).
//   - layoutVersion evolution contract: unknown-higher fails closed on write/pack.
//   - The marker is excluded from the D30 component allowlist scan (no phantom component).
//   - Forward-compat: a future `authoringModel:` field in settings.mcs.yml round-trips via
//     OM ExtensionData (pinned OM 2026.5.3), not dropped/choked.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentNodeQ2MarkerTests
{
    private static readonly AgentFilePath MarkerPath = new AgentFilePath("agent.sync.yaml");
    private static readonly AgentFilePath SettingsPath = new AgentFilePath("settings.mcs.yml");

    // --- layoutVersion parser -------------------------------------------------------

    [Theory]
    [InlineData("layoutVersion: 1\n", 1)]
    [InlineData("# header\nlayoutVersion: 1\n", 1)]
    [InlineData("layoutVersion: 2  # future\n", 2)]
    [InlineData("layoutVersion:    7\n", 7)]
    [InlineData("", null)]
    [InlineData("schemaName: x\n", null)]
    [InlineData("layoutVersion: notanint\n", null)]
    [InlineData("  layoutVersion: 1\n", null)] // indented -> not top-level
    public void TryParseLayoutVersion_Cases(string text, int? expected)
    {
        Assert.Equal(expected, AgentClassifier.TryParseLayoutVersion(text));
    }

    // --- Marker emission on clone ---------------------------------------------------

    [Fact]
    public async Task Clone_CliAgent_EmitsLayoutMarker_Version1()
    {
        var (_, _, accessor, _, _) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        Assert.True(accessor.Exists(MarkerPath), "CLI clone must emit agent.sync.yaml.");
        Assert.Equal(AgentClassifier.CurrentLayoutVersion, CliAgentBotEntityReader.TryReadLayoutVersion(accessor));

        var body = await accessor.ReadStringAsync(MarkerPath, CancellationToken.None);
        Assert.Contains("layoutVersion: 1", body);
        // Layout-only: no identity/shape echo.
        Assert.DoesNotContain("schemaName", body);
        Assert.DoesNotContain("agentSettings", body);
    }

    [Fact]
    public async Task Clone_ClassicAgent_EmitsNoSyncFiles()
    {
        var (_, _, accessor, _, _) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");

        Assert.False(accessor.Exists(MarkerPath), "Classic clone must NOT emit agent.sync.yaml.");
        Assert.Null(CliAgentBotEntityReader.TryReadLayoutVersion(accessor));
        Assert.DoesNotContain(accessor.Files.Keys,
            k => k.EndsWith(".sync.yaml", StringComparison.OrdinalIgnoreCase));
    }

    // --- Marker excluded from the component scan (no phantom) ------------------------

    [Fact]
    public async Task Clone_CliAgent_MarkerIsNotScannedAsComponent()
    {
        var (_, definition, _, sync, workspace) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var originalSchemas = definition.Components.Select(c => c.SchemaNameString).ToHashSet(StringComparer.Ordinal);
        var readSchemas = read.Components.Select(c => c.SchemaNameString).ToHashSet(StringComparer.Ordinal);
        Assert.True(readSchemas.SetEquals(originalSchemas),
            "The agent.sync.yaml marker must not be read as a component (no phantom add/drop).");
    }

    // --- Marker-aware DetectWorkspaceLayout (real-disk temp workspaces) --------------

    [Fact]
    public async Task DetectWorkspaceLayout_MarkerPresent_CliContent_IsCliLayered()
    {
        var settings = await CliSettingsContentAsync();
        RunInTempWorkspace(settings, markerVersion: 1, dir =>
            Assert.Equal(WorkspaceLayout.CliLayered, AgentClassifier.DetectWorkspaceLayout(dir)));
    }

    [Fact]
    public async Task DetectWorkspaceLayout_NoMarker_CliContent_IsCliLayered_TransitionFallback()
    {
        var settings = await CliSettingsContentAsync();
        RunInTempWorkspace(settings, markerVersion: null, dir =>
            Assert.Equal(WorkspaceLayout.CliLayered, AgentClassifier.DetectWorkspaceLayout(dir)));
    }

    [Fact]
    public async Task DetectWorkspaceLayout_NoMarker_ClassicContent_IsClassicMcs()
    {
        var settings = await ClassicSettingsContentAsync();
        RunInTempWorkspace(settings, markerVersion: null, dir =>
            Assert.Equal(WorkspaceLayout.ClassicMcs, AgentClassifier.DetectWorkspaceLayout(dir)));
    }

    [Fact]
    public async Task DetectWorkspaceLayout_MarkerCli_ButClassicContent_FailsClosed_Unknown()
    {
        // D29: the marker is layout-authoritative but NEVER shape-authoritative. A marker
        // claiming the CLI layout over non-CLI content (tamper/corruption) fails closed.
        var settings = await ClassicSettingsContentAsync();
        RunInTempWorkspace(settings, markerVersion: 1, dir =>
            Assert.Equal(WorkspaceLayout.Unknown, AgentClassifier.DetectWorkspaceLayout(dir)));
    }

    [Fact]
    public async Task DetectWorkspaceLayout_MarkerUnknownHigher_FallsBackToContent()
    {
        // Unknown-higher version: best-effort read via content inference; the write/pack
        // path fails closed separately (see PushChangeset test below).
        var settings = await CliSettingsContentAsync();
        RunInTempWorkspace(settings, markerVersion: 2, dir =>
            Assert.Equal(WorkspaceLayout.CliLayered, AgentClassifier.DetectWorkspaceLayout(dir)));
    }

    // --- Evolution contract: unknown-higher fails closed on write/pack --------------

    [Fact]
    public void HasUnsupportedHigherLayoutVersion_Cases()
    {
        var v2 = SeedMarkerAccessor(2);
        Assert.True(CliAgentBotEntityReader.HasUnsupportedHigherLayoutVersion(v2, out var got));
        Assert.Equal(2, got);

        var v1 = SeedMarkerAccessor(1);
        Assert.False(CliAgentBotEntityReader.HasUnsupportedHigherLayoutVersion(v1, out _));

        var none = (InMemoryFileAccessor)new InMemoryFileAccessorFactory().Create(new DirectoryPath("c:/test/none/"));
        Assert.False(CliAgentBotEntityReader.HasUnsupportedHigherLayoutVersion(none, out _));
    }

    [Fact]
    public async Task PushChangeset_UnknownHigherLayoutVersion_FailsClosed()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/q2-version-gate-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);
        await accessor.WriteAsync(MarkerPath, "layoutVersion: 2\n", CancellationToken.None);

        var changeset = new PvaComponentChangeSet(
            new List<BotComponentChange>(), bot: null, changeToken: "t");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            synchronizer.PushChangesetAsync(
                workspace,
                ComponentWriterDefensiveTests.CreateMockOperationContext(),
                changeset,
                new Mock<ISyncDataverseClient>().Object,
                Guid.NewGuid(),
                null,
                default,
                CancellationToken.None));
    }

    // --- Forward-compat: future settings.mcs.yml field round-trips via ExtensionData -

    [Fact]
    public void Settings_FutureAuthoringModelField_RoundTripsViaExtensionData()
    {
        // A consumer pinned to OM 2026.5.3 reading a settings.mcs.yml that carries a FUTURE
        // `authoringModel:` field must preserve it (round-trip via OM ExtensionData), not
        // drop or choke. agent.sync.yaml (the layout axis) is INVARIANT across this future
        // OM content event - a content field moves no files (D29 bump rule).
        const string yaml = "kind: Bot\nschemaName: Test_fwd_compat\nauthoringModel: futureSignalV2\n";

        var entity = CodeSerializer.Deserialize<BotEntity>(yaml);
        Assert.NotNull(entity);
        Assert.Equal("Test_fwd_compat", entity!.SchemaName.Value);

        var reserialized = CodeSerializer.Serialize(entity);
        Assert.Contains("authoringModel", reserialized);
        Assert.Contains("futureSignalV2", reserialized);
    }

    // --- Helpers --------------------------------------------------------------------

    private static async Task<string> CliSettingsContentAsync()
    {
        var (_, _, accessor, _, _) = await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        return await accessor.ReadStringAsync(SettingsPath, CancellationToken.None);
    }

    private static async Task<string> ClassicSettingsContentAsync()
    {
        var (_, _, accessor, _, _) = await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");
        return await accessor.ReadStringAsync(SettingsPath, CancellationToken.None);
    }

    private static void RunInTempWorkspace(string settingsContent, int? markerVersion, Action<string> assert)
    {
        var dir = Path.Combine(Path.GetTempPath(), "q2dwl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "settings.mcs.yml"), settingsContent);
            if (markerVersion.HasValue)
            {
                File.WriteAllText(Path.Combine(dir, "agent.sync.yaml"), $"layoutVersion: {markerVersion}\n");
            }

            assert(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private static InMemoryFileAccessor SeedMarkerAccessor(int version)
    {
        var accessor = (InMemoryFileAccessor)new InMemoryFileAccessorFactory()
            .Create(new DirectoryPath($"c:/test/marker-{version}-{Guid.NewGuid():N}/"));
        accessor.WriteAsync(MarkerPath, $"layoutVersion: {version}\n", CancellationToken.None).GetAwaiter().GetResult();
        return accessor;
    }
}
