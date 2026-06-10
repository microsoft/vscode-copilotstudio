// Copyright (C) Microsoft Corporation. All rights reserved.
//
// CliAgentSyncSupport / Node D5 — tests for the CLI agent connection-
// references writer (infrastructure/connections/{name}.sync.yaml) and the
// kind-aware dispatch in WorkspaceSynchronizer.WriteConnectionReferencesAsync
// that routes the typed ConnectionReference collection to it.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.ObjectModel.Yaml;
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
using Xunit;
using Xunit.Abstractions;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Tests for <see cref="CliAgentConnectionsWriter"/> and the CLI-connection
/// dispatch added to <c>WorkspaceSynchronizer.WriteConnectionReferencesAsync</c>
/// (Node D5).
///
/// Unlike D2/D3/D4 the dispatch site is NOT inside <c>ComponentWriter.Write</c>;
/// <see cref="ConnectionReference"/> is not a <see cref="BotComponentBase"/>
/// (lives on <see cref="DefinitionBase.ConnectionReferences"/>), so the
/// branch lives inside the existing <c>WriteConnectionReferencesAsync</c>
/// helper called once per <c>UpdateWorkspaceDirectoryAsync</c>.
///
/// Goals (from cli-agent-sync-support roadmap / continuation D5):
/// <list type="bullet">
///   <item>Writer-level positive: each FoodLogger connection reference
///         lands as <c>infrastructure/connections/{logicalName}.sync.yaml</c>
///         with byte-stable output across repeated writes (field round-trip
///         fidelity is covered by the reader Overlay tests).</item>
///   <item>Writer-level negative: empty/null logical name or
///         path-separator-bearing logical name skip with warning; valid
///         peers still process.</item>
///   <item>Writer-level null-tolerance: a null input collection runs the
///         prune step without throwing.</item>
///   <item>Filename projection: the full
///         <c>ConnectionReferenceLogicalName</c> is the filename (no
///         prefix-strip, dots preserved); separator-bearing names throw.</item>
///   <item>Stale-orphan cleanup: a prior write that produced
///         <c>infrastructure/connections/foo.sync.yaml</c> is pruned when the
///         next write doesn't include "foo".</item>
///   <item>Strict parent filter: nested
///         <c>infrastructure/connections/sub/x.sync.yaml</c> survives prune.</item>
///   <item>Dispatch positive (CLI): FoodLogger push routes connections
///         to <c>infrastructure/connections/</c> and SUPPRESSES the flat
///         <c>connectionreferences.mcs.yml</c>.</item>
///   <item>Dispatch stale-flat (CLI): pre-seeded flat
///         <c>connectionreferences.mcs.yml</c> is removed when the CLI
///         dispatch writes (even with 0 references).</item>
///   <item>Dispatch zero-ref (CLI): BrandSpecialist push produces no flat
///         file AND no <c>infrastructure/</c> directory.</item>
///   <item>Classic regression #1: HRAgent (0 refs) push writes no flat
///         file AND no <c>infrastructure/</c> directory.</item>
///   <item>Classic regression #2: synthetic classic-template entity with
///         a connection reference STILL writes the flat
///         <c>connectionreferences.mcs.yml</c> and produces no
///         <c>infrastructure/</c> output.</item>
/// </list>
/// </summary>
public class CliAgentConnectionsWriterTests
{
    // FoodLogger has 2 ConnectionReferences:
    //   1. Dataverse: new_sharedcommondataserviceforapps_6480c125
    //   2. WorkIQ SharePoint (logical name contains dots):
    //      Default_draft_rxzs_q.cr.shared_workiqsharepoint.020c62e5181149bc8c90e45269f66dce
    private const string FoodLoggerDataverseLogicalName =
        "new_sharedcommondataserviceforapps_6480c125";
    private const string FoodLoggerSharePointLogicalName =
        "Default_draft_rxzs_q.cr.shared_workiqsharepoint.020c62e5181149bc8c90e45269f66dce";

    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");
    private static readonly AgentFilePath FlatConnectionReferencesPath =
        new AgentFilePath("connectionreferences.mcs.yml");

    private static readonly string TestDataResourcePrefix =
        typeof(CliAgentConnectionsWriterTests).Assembly.GetName().Name + ".TestData.CliAgentFixtures.";

    private readonly ITestOutputHelper _output;

    public CliAgentConnectionsWriterTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // --- Writer-level tests ---------------------------------------------------------

    [Fact]
    public void Writer_FoodLoggerDataverseConnection_WritesYamlToInfrastructure()
    {
        var cr = GetFoodLoggerConnectionReference(FoodLoggerDataverseLogicalName);

        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(accessor, new[] { cr }, null, CancellationToken.None);

        var expectedPath = new AgentFilePath(
            $"infrastructure/connections/{FoodLoggerDataverseLogicalName}.sync.yaml");
        Assert.True(accessor.Exists(expectedPath),
            $"Dataverse connection reference should land at {expectedPath}.");
    }

    [Fact]
    public void Writer_FoodLoggerSharePointConnection_WritesYamlToInfrastructure()
    {
        // Logical name contains 4 dots — must be preserved verbatim in the
        // filename (no segment-stripping, unlike D2/D3/D4).
        var cr = GetFoodLoggerConnectionReference(FoodLoggerSharePointLogicalName);

        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(accessor, new[] { cr }, null, CancellationToken.None);

        var expectedPath = new AgentFilePath(
            $"infrastructure/connections/{FoodLoggerSharePointLogicalName}.sync.yaml");
        Assert.True(accessor.Exists(expectedPath),
            $"SharePoint connection reference (with dotted logical name) should land at {expectedPath}.");
    }

    [Theory]
    [InlineData(FoodLoggerDataverseLogicalName)]
    [InlineData(FoodLoggerSharePointLogicalName)]
    public void Writer_ConnectionRef_RepeatedWrites_AreByteStable(string logicalName)
    {
        // No-spurious-push-changes invariant: writing the same reference twice must
        // produce identical bytes (write idempotence). This locks the real constraint
        // (a stable, canonical per-file output so push does not detect phantom changes)
        // WITHOUT coupling to the OM serializer's exact formatting. Field-level round-trip
        // fidelity is covered by the CliAgentConnectionsReader Overlay tests.
        var cr = GetFoodLoggerConnectionReference(logicalName);
        var path = $"infrastructure/connections/{logicalName}.sync.yaml";

        var a1 = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(a1, new[] { cr }, null, CancellationToken.None);
        var a2 = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(a2, new[] { cr }, null, CancellationToken.None);

        Assert.True(a1.Files[path].SequenceEqual(a2.Files[path]),
            "Repeated writes of the same connection reference must be byte-stable (no spurious push changes).");
    }

    [Fact]
    public void WriteAll_NullCollection_DoesNotThrow()
    {
        // Null tolerance contract — rubber-duck D5 blocking #2.
        // The defensive "0-refs, prune only" caller path passes null
        // through WriteAll without an NRE.
        var accessor = NewAccessor();

        var ex = Record.Exception(() =>
            CliAgentConnectionsWriter.WriteAll(accessor, null, null, CancellationToken.None));

        Assert.Null(ex);
        Assert.Empty(accessor.Files);
    }

    [Fact]
    public void WriteAll_EmptyCollection_DoesNotCreateDirectory()
    {
        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(
            accessor, Enumerable.Empty<ConnectionReference>(), null, CancellationToken.None);

        Assert.Empty(accessor.Files);
    }

    [Fact]
    public void WriteAll_DedupesByLogicalName_LastWins()
    {
        // Mirrors WriteConnectionReferencesAsync's existing dedup behavior:
        // OrdinalIgnoreCase group-by → last wins. Synthetic 2 refs with
        // same logical name, different connector IDs → only one file
        // written, containing the second's connectorId.
        var cr1 = MakeSyntheticConnectionReference(
            logicalName: "dup_name",
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_first");
        var cr2 = MakeSyntheticConnectionReference(
            logicalName: "DUP_NAME",  // differs only in case
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_second");

        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(accessor, new[] { cr1, cr2 }, null, CancellationToken.None);

        // Exactly one file exists under infrastructure/connections/.
        var underFolder = accessor.Files.Keys
            .Where(k => k.StartsWith("infrastructure/connections/", StringComparison.Ordinal))
            .ToList();
        Assert.Single(underFolder);

        // Content reflects the last-wins logical name AND connector ID.
        var content = Encoding.UTF8.GetString(accessor.Files[underFolder.Single()]);
        Assert.Contains("DUP_NAME", content);
        Assert.Contains("shared_second", content);
        Assert.DoesNotContain("shared_first", content);
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData(@"bad\name")]
    public void WriteAll_LogicalNameWithPathSeparator_SkipsWithWarning_OtherWritesProceed(string badName)
    {
        // Rubber-duck D5 blocking #1: a single invalid logical name (containing a path
        // separator) must NOT abort writes for valid peers. The validate-and-project step
        // catches it, warns, and lets the good ref still land. Covers both separators.
        var bad = MakeSyntheticConnectionReference(
            logicalName: badName,
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_bad");
        var good = MakeSyntheticConnectionReference(
            logicalName: "good_name",
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_good");

        var warnings = new List<string>();
        var accessor = NewAccessor();
        CliAgentConnectionsWriter.WriteAll(accessor, new[] { bad, good }, warnings.Add, CancellationToken.None);

        // Good ref written; the bad ref produced no file (only good_name under the folder).
        Assert.True(accessor.Exists(new AgentFilePath("infrastructure/connections/good_name.sync.yaml")));
        Assert.Single(accessor.Files.Keys.Where(k =>
            k.StartsWith("infrastructure/connections/", StringComparison.Ordinal)));
        // Warning surfaced for the bad name.
        Assert.Contains(warnings, w => w.Contains(badName, StringComparison.Ordinal));
    }

    [Fact]
    public void WriteAll_OrphanInExpectedFolder_RemovedOnRewrite()
    {
        // Stale-cleanup core invariant. Pre-seed an orphan file at
        // infrastructure/connections/old.sync.yaml; write a single ref with a
        // different name → the orphan is gone.
        var accessor = NewAccessor();
        WriteSeed(accessor,
            new AgentFilePath("infrastructure/connections/old.sync.yaml"),
            "# orphan from prior write\n");

        var cr = MakeSyntheticConnectionReference(
            logicalName: "new_name",
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_new");

        CliAgentConnectionsWriter.WriteAll(accessor, new[] { cr }, null, CancellationToken.None);

        Assert.False(accessor.Exists(new AgentFilePath("infrastructure/connections/old.sync.yaml")));
        Assert.True(accessor.Exists(new AgentFilePath("infrastructure/connections/new_name.sync.yaml")));
    }

    [Fact]
    public void WriteAll_OrphanWithEmptyInput_StillRemoved()
    {
        // Zero-ref case still runs the prune step. Pre-seed an orphan,
        // write empty collection → orphan removed.
        var accessor = NewAccessor();
        WriteSeed(accessor,
            new AgentFilePath("infrastructure/connections/orphan.sync.yaml"),
            "# orphan\n");

        CliAgentConnectionsWriter.WriteAll(
            accessor, Enumerable.Empty<ConnectionReference>(), null, CancellationToken.None);

        Assert.False(accessor.Exists(new AgentFilePath("infrastructure/connections/orphan.sync.yaml")));
        Assert.Empty(accessor.Files);
    }

    [Fact]
    public void WriteAll_StrictParentFilter_DoesNotPruneNestedYaml()
    {
        // Rubber-duck non-blocking #6: the disk ListFiles uses
        // SearchOption.AllDirectories, so the prune step must filter to
        // direct children only. Pre-seed a nested
        // infrastructure/connections/sub/x.sync.yaml; rewrite with a new ref →
        // the nested file survives.
        var accessor = NewAccessor();
        WriteSeed(accessor,
            new AgentFilePath("infrastructure/connections/sub/nested.sync.yaml"),
            "# nested\n");

        var cr = MakeSyntheticConnectionReference(
            logicalName: "rewrite_target",
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_x");
        CliAgentConnectionsWriter.WriteAll(accessor, new[] { cr }, null, CancellationToken.None);

        Assert.True(accessor.Exists(new AgentFilePath("infrastructure/connections/sub/nested.sync.yaml")),
            "Nested files under sub-directories must survive prune (D5 does not own them).");
        Assert.True(accessor.Exists(new AgentFilePath("infrastructure/connections/rewrite_target.sync.yaml")));
    }

    [Fact]
    public void WriteAll_ExistingFileWithSameLogicalName_OverwrittenNotPruned()
    {
        // Confirm that an existing file matching the expected name is
        // overwritten by the new content (not deleted by prune then
        // re-written empty).
        var accessor = NewAccessor();
        WriteSeed(accessor,
            new AgentFilePath("infrastructure/connections/keeper.sync.yaml"),
            "# old content that should be replaced\n");

        var cr = MakeSyntheticConnectionReference(
            logicalName: "keeper",
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_new");
        CliAgentConnectionsWriter.WriteAll(accessor, new[] { cr }, null, CancellationToken.None);

        var content = Encoding.UTF8.GetString(
            accessor.Files["infrastructure/connections/keeper.sync.yaml"]);
        Assert.DoesNotContain("old content", content);
        Assert.Contains("shared_new", content);
    }

    // --- Filename projection theory --------------------------------------------------

    [Theory]
    [InlineData(
        "new_sharedcommondataserviceforapps_6480c125",
        "new_sharedcommondataserviceforapps_6480c125.sync.yaml")]
    [InlineData(
        "Default_draft_rxzs_q.cr.shared_workiqsharepoint.020c62e5181149bc8c90e45269f66dce",
        "Default_draft_rxzs_q.cr.shared_workiqsharepoint.020c62e5181149bc8c90e45269f66dce.sync.yaml")]
    [InlineData("simple", "simple.sync.yaml")]
    [InlineData("one.dot", "one.dot.sync.yaml")]
    public void ComputeConnectionFileName_PreservesLogicalNameVerbatim(
        string logicalName, string expectedFilename)
    {
        Assert.Equal(expectedFilename, CliAgentConnectionsWriter.ComputeConnectionFileName(logicalName));
    }

    [Fact]
    public void ComputeConnectionFileName_EmptyLogicalName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            CliAgentConnectionsWriter.ComputeConnectionFileName(""));
    }

    [Theory]
    [InlineData("bad/name")]
    [InlineData(@"bad\name")]
    public void ComputeConnectionFileName_PathSeparatorInName_Throws(string badName)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CliAgentConnectionsWriter.ComputeConnectionFileName(badName));
        Assert.Contains("path separator", ex.Message);
    }

    // --- Dispatch tests --------------------------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task
        Dispatch_CliAgent_FoodLogger_WritesConnectionsToInfrastructure_AndSuppressesFlatFile()
    {
        // Full FoodLogger push (entity + components + 2 connection refs).
        // Both refs land at infrastructure/connections/{name}.sync.yaml;
        // no connectionreferences.mcs.yml at root.
        var (entity, definition) = LoadFixtureBotAndDefinition("FoodLogger");

        var componentChanges = definition.Components
            .Select(c => (BotComponentChange)new BotComponentInsert(c))
            .ToList();
        var crChanges = definition.ConnectionReferences
            .Select(cr => (ConnectionReferenceChange)new ConnectionReferenceInsert(cr))
            .ToList();
        Assert.Equal(2, crChanges.Count);

        var accessor = await RunPushWithChangesetAsync(entity, componentChanges, crChanges);

        Assert.True(accessor.Exists(new AgentFilePath(
            $"infrastructure/connections/{FoodLoggerDataverseLogicalName}.sync.yaml")),
            "Dataverse connection reference should land at infrastructure/connections/.");
        Assert.True(accessor.Exists(new AgentFilePath(
            $"infrastructure/connections/{FoodLoggerSharePointLogicalName}.sync.yaml")),
            "SharePoint connection reference should land at infrastructure/connections/.");

        Assert.False(accessor.Exists(FlatConnectionReferencesPath),
            "Flat connectionreferences.mcs.yml must NOT exist for CLI agents.");
    }

    [Fact]
    public async System.Threading.Tasks.Task
        Dispatch_CliAgent_StaleFlatConnectionReferences_RemovedOnWrite()
    {
        // Pre-seed a stale flat connectionreferences.mcs.yml (as if the
        // workspace was previously cloned via the classic-shape pac-demo).
        // When CLI dispatch fires, the flat file must be removed (TDD D33:
        // after the per-file set is written+committed, the stale flat is
        // removed; the end state is no flat file).
        var (entity, definition) = LoadFixtureBotAndDefinition("FoodLogger");

        var componentChanges = definition.Components
            .Select(c => (BotComponentChange)new BotComponentInsert(c))
            .ToList();
        var crChanges = definition.ConnectionReferences
            .Select(cr => (ConnectionReferenceChange)new ConnectionReferenceInsert(cr))
            .ToList();

        var (synchronizer, factory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/dispatch-cr-stale-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "seed", CancellationToken.None);

        // Seed the stale flat file.
        await accessor.WriteAsync(FlatConnectionReferencesPath,
            Encoding.UTF8.GetBytes("# stale classic-shape file\nconnectionReferences:\n  []\n"),
            CancellationToken.None);
        Assert.True(accessor.Exists(FlatConnectionReferencesPath));

        var changeset = new PvaComponentChangeSet(
            botComponentChanges: componentChanges,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: crChanges,
            aIPluginOperationChanges: null,
            componentCollectionChanges: null,
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: entity,
            changeToken: "after-write");
        mockIsland.Setup(x => x.SaveChangesAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<PvaComponentChangeSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(changeset);

        await synchronizer.PushChangesetAsync(
            workspace, ComponentWriterDefensiveTests.CreateMockOperationContext(),
            changeset, new Mock<ISyncDataverseClient>().Object,
            Guid.NewGuid(), null, default, CancellationToken.None);

        Assert.False(accessor.Exists(FlatConnectionReferencesPath),
            "Stale flat connectionreferences.mcs.yml must be removed when the CLI dispatch runs.");

        // Sanity: CLI files were written.
        Assert.True(accessor.Exists(new AgentFilePath(
            $"infrastructure/connections/{FoodLoggerDataverseLogicalName}.sync.yaml")));
    }

    [Fact]
    public async System.Threading.Tasks.Task
        Dispatch_CliAgent_BrandSpecialist_NoInfrastructureDirectory_NoFlatFile()
    {
        // BrandSpecialist (CLI smoke fixture) has 0 ConnectionReferences.
        // Neither output shape should appear.
        var (entity, definition) = LoadFixtureBotAndDefinition("BrandSpecialist");
        Assert.Empty(definition.ConnectionReferences);

        var componentChanges = definition.Components
            .Select(c => (BotComponentChange)new BotComponentInsert(c))
            .ToList();

        var accessor = await RunPushWithChangesetAsync(
            entity, componentChanges, Enumerable.Empty<ConnectionReferenceChange>().ToList());

        Assert.False(accessor.Exists(FlatConnectionReferencesPath));
        Assert.DoesNotContain(accessor.Files.Keys,
            k => k.StartsWith("infrastructure/connections/", StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task
        Dispatch_ClassicAgent_HRAgent_NoInfrastructureDirectory_NoFlatFile()
    {
        // Classic regression #1 (HRAgent has 0 ConnectionReferences):
        // proves CLI dispatch is gated AND classic dispatch doesn't
        // accidentally create infrastructure/ output for 0-ref agents.
        var (entity, definition) = LoadFixtureBotAndDefinition("HRAgent");
        Assert.False(
            entity.Template?.StartsWith("cliagent-", StringComparison.OrdinalIgnoreCase) == true,
            "HRAgent fixture invariant: template must not have cliagent- prefix.");
        Assert.Empty(definition.ConnectionReferences);

        var componentChanges = definition.Components
            .Select(c => (BotComponentChange)new BotComponentInsert(c))
            .ToList();

        var accessor = await RunPushWithChangesetAsync(
            entity, componentChanges, Enumerable.Empty<ConnectionReferenceChange>().ToList());

        Assert.False(accessor.Exists(FlatConnectionReferencesPath));
        Assert.DoesNotContain(accessor.Files.Keys,
            k => k.StartsWith("infrastructure/", StringComparison.Ordinal));
    }

    [Fact]
    public async System.Threading.Tasks.Task
        Dispatch_SyntheticClassicAgentWithConnectionReferences_WritesFlatFileOnly()
    {
        // Classic regression #2 (positive): a classic-template entity with
        // a connection reference MUST still emit the flat
        // connectionreferences.mcs.yml AND must NOT produce any
        // infrastructure/ output. This is the R3 ("classic untouched")
        // guard that HRAgent's 0-ref fixture can't provide.
        var classicEntity = CodeSerializer.Deserialize<BotEntity>(
            "kind: Bot\nschemaName: ClassicAgentSchema\ntemplate: default-2.1.0\n")!;
        var cr = MakeSyntheticConnectionReference(
            logicalName: "classic_dataverse_ref",
            connectorId: "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps");

        var crChanges = new List<ConnectionReferenceChange>
        {
            new ConnectionReferenceInsert(cr),
        };

        var accessor = await RunPushWithChangesetAsync(
            classicEntity, new List<BotComponentChange>(), crChanges);

        // Flat file MUST exist for classic agents with connection refs.
        Assert.True(accessor.Exists(FlatConnectionReferencesPath),
            "Classic agent with connection references must still emit flat connectionreferences.mcs.yml.");

        var flatContent = Encoding.UTF8.GetString(accessor.Files[FlatConnectionReferencesPath.ToString()]);
        Assert.Contains("classic_dataverse_ref", flatContent);

        // No infrastructure/ output anywhere.
        var infrastructureFiles = accessor.Files.Keys
            .Where(k => k.StartsWith("infrastructure/", StringComparison.Ordinal))
            .ToList();
        Assert.True(infrastructureFiles.Count == 0,
            "Classic agent push must not produce any infrastructure/ output. Found: "
            + string.Join(", ", infrastructureFiles));
    }

    // --- Helpers ---------------------------------------------------------------------

    private static void WriteSeed(InMemoryFileAccessor accessor, AgentFilePath path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = accessor.OpenWrite(path);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async System.Threading.Tasks.Task<InMemoryFileAccessor>
        RunPushWithChangesetAsync(
            BotEntity entity,
            IReadOnlyList<BotComponentChange> componentChanges,
            IReadOnlyList<ConnectionReferenceChange> connectionReferenceChanges)
    {
        var (synchronizer, factory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/dispatch-cr-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "seed", CancellationToken.None);

        var confirmationChangeset = new PvaComponentChangeSet(
            botComponentChanges: componentChanges,
            connectorDefinitionChanges: null,
            environmentVariableChanges: null,
            connectionReferenceChanges: connectionReferenceChanges,
            aIPluginOperationChanges: null,
            componentCollectionChanges: null,
            dataverseTableSearchChanges: null,
            dataverseTableSearchEntityConfigurationChanges: null,
            connectedAgentDefinitionChanges: null,
            bot: entity,
            changeToken: "next-token");

        mockIsland.Setup(x => x.SaveChangesAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<PvaComponentChangeSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(confirmationChangeset);

        await synchronizer.PushChangesetAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            confirmationChangeset,
            new Mock<ISyncDataverseClient>().Object,
            Guid.NewGuid(),
            null,
            default,
            CancellationToken.None);

        return accessor;
    }

    private static ConnectionReference GetFoodLoggerConnectionReference(string logicalName)
    {
        var (_, def) = LoadFixtureBotAndDefinition("FoodLogger");
        return def.ConnectionReferences
            .Single(cr => cr.ConnectionReferenceLogicalName.Value == logicalName);
    }

    /// <summary>
    /// Build a minimal ConnectionReference via the JSON round-trip path,
    /// so the typed object goes through the same construction route as a
    /// real cloud-cache read (matches D3's MakeSyntheticKnowledgeComponent
    /// pattern).
    /// </summary>
    private static ConnectionReference MakeSyntheticConnectionReference(
        string logicalName,
        string connectorId)
    {
        var json = $$"""
        {
          "$kind": "BotDefinition",
          "connectionReferences": [
            {
              "$kind": "ConnectionReference",
              "connectionReferenceLogicalName": {{JsonSerializer.Serialize(logicalName)}},
              "connectorId": {{JsonSerializer.Serialize(connectorId)}}
            }
          ]
        }
        """;

        var bytes = Encoding.UTF8.GetBytes(json);
        var accessor = NewAccessor();
        using (var s = accessor.OpenWrite(CachePath))
        {
            s.Write(bytes, 0, bytes.Length);
        }
        var def = (BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
        return def.ConnectionReferences.Single();
    }

    private static (BotEntity entity, BotDefinition definition) LoadFixtureBotAndDefinition(string fixtureName)
    {
        var bytes = LoadFixtureBytes(fixtureName);
        var accessor = NewAccessor();
        using (var s = accessor.OpenWrite(CachePath))
        {
            s.Write(bytes, 0, bytes.Length);
        }
        var def = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)
                  ?? throw new InvalidOperationException($"ReadCloudCacheSnapshot returned null for {fixtureName}.");
        var botDef = (BotDefinition)def;
        return (botDef.Entity!, botDef);
    }

    private static byte[] LoadFixtureBytes(string fixtureName)
    {
        var resourceName = TestDataResourcePrefix + fixtureName + ".botdefinition.json";
        using var stream = typeof(CliAgentConnectionsWriterTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static InMemoryFileAccessor NewAccessor() =>
        new InMemoryFileAccessor(new DirectoryPath($"c:/test/connections-writer-{Guid.NewGuid():N}/"));
}
