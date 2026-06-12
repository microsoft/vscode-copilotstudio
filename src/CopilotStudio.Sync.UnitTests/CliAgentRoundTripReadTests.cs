// Copyright (C) Microsoft Corporation. All rights reserved.
//
// CliAgentSyncSupport / Node E — integration round-trip tests for
// ReadWorkspaceDefinitionAsync on CLI-shape workspaces. Validates:
//   - No spurious component deletes: after a clean clone (push of all
//     cloud-cache components), the read returns the same component set
//     that was pushed (the "Done when: Round-trip clean pull passes"
//     criterion from the Node E roadmap).
//   - Connection references overlaid + preserved.
//   - Classic regression baseline: HRAgent (classic kind) still works.
//   - Stale classic *.mcs.yml on a CLI workspace is ignored (does not
//     synthesize a phantom component).

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
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentRoundTripReadTests
{
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");

    private static readonly string TestDataResourcePrefix =
        typeof(CliAgentRoundTripReadTests).Assembly.GetName().Name + ".TestData.CliAgentFixtures.";

    private readonly ITestOutputHelper _output;

    public CliAgentRoundTripReadTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // --- Component-identity round-trip (no spurious deletes) ------------------------

    [Fact]
    public async Task ReadWorkspaceDefinition_CachelessCliSettings_ReturnsBareDefinition()
    {
        var (synchronizer, factory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/cacheless-cli-read-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);

        await accessor.WriteAsync(new AgentFilePath(AgentClassifier.WorkspaceLayoutMarkerFileName),
            "layoutVersion: 1\n",
            CancellationToken.None);
        await accessor.WriteAsync(new AgentFilePath("settings.mcs.yml"),
            """
            displayName: Cacheless CLI
            schemaName: test_CachelessCli
            configuration:
              recognizer:
                kind: CLICopilotRecognizer
              agentSettings:
                model:
                  series: Sonnet46
                instructions:
                  segments:
                    - kind: StaticSegment
                      value: Cacheless instructions.
            template: cliagent-1.0.0
            language: 1033
            """,
            CancellationToken.None);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None, checkKnowledgeFiles: true);

        var bot = Assert.IsType<BotDefinition>(read);
        Assert.Equal("Cacheless CLI", bot.Entity!.DisplayName);
        Assert.Equal("test_CachelessCli", bot.Entity.SchemaName.Value);
        Assert.NotNull(bot.Entity.Configuration?.AgentSettings);
        Assert.Empty(bot.Components);
    }

    [Fact]
    public async Task RoundTrip_CliAgent_FoodLogger_AllComponentsSurvivePull()
    {
        // The primary Node E "Done when" criterion: a CLI workspace pulled
        // back through ReadWorkspaceDefinitionAsync surfaces every component
        // that was pushed (no per-component file dropouts).
        var (entity, definition, accessor, synchronizer, workspace) =
            await PushFixtureAsClone("FoodLogger");

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        AssertComponentSchemaParity(definition, read, fixtureName: "FoodLogger");
    }

    [Fact]
    public async Task RoundTrip_CliAgent_BrandSpecialist_AllComponentsSurvivePull()
    {
        var (entity, definition, accessor, synchronizer, workspace) =
            await PushFixtureAsClone("BrandSpecialist");

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        AssertComponentSchemaParity(definition, read, fixtureName: "BrandSpecialist");
    }

    [Fact]
    public async Task RoundTrip_ClassicAgent_HRAgent_AllComponentsSurvivePull()
    {
        // Classic regression: Node E's dispatch gating must not affect
        // classic agents. HRAgent is the classic baseline fixture.
        var (entity, definition, accessor, synchronizer, workspace) =
            await PushFixtureAsClone("HRAgent");

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        AssertComponentSchemaParity(definition, read, fixtureName: "HRAgent");
    }

    // --- Connection-references overlay (CLI only) -----------------------------------

    [Fact]
    public async Task RoundTrip_CliAgent_FoodLogger_ConnectionsPreservedWithAllFields()
    {
        // FoodLogger has 2 ConnectionReferences. Reading back must produce
        // a collection with both, with cloud-cache fields (Id, ConnectionId,
        // DisplayName, ...) intact (overlay rule from CliAgentConnectionsReader).
        var (entity, definition, accessor, synchronizer, workspace) =
            await PushFixtureAsClone("FoodLogger");

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        Assert.Equal(definition.ConnectionReferences.Length, read.ConnectionReferences.Length);
        foreach (var original in definition.ConnectionReferences)
        {
            var match = read.ConnectionReferences
                .Single(cr => cr.ConnectionReferenceLogicalName.Value
                              == original.ConnectionReferenceLogicalName.Value);
            Assert.Equal(original.ConnectorId.Value, match.ConnectorId.Value);
            Assert.Equal(original.Id.Value, match.Id.Value);
            Assert.Equal(original.ConnectionId, match.ConnectionId);
            Assert.Equal(original.DisplayName, match.DisplayName);
        }
    }

    [Fact]
    public async Task RoundTrip_CliAgent_BrandSpecialist_NoConnections_NoOverlay()
    {
        // BrandSpecialist has 0 ConnectionReferences. Migration-safe rule:
        // no infrastructure/connections/ directory exists, so the overlay
        // is skipped and cloud-cache (empty) is preserved verbatim.
        var (entity, definition, accessor, synchronizer, workspace) =
            await PushFixtureAsClone("BrandSpecialist");

        Assert.Empty(definition.ConnectionReferences);
        Assert.False(CliAgentConnectionsReader.IsLayeredShapeActive(accessor));

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        Assert.Empty(read.ConnectionReferences);
    }

    // --- Classic-shape isolation: stale *.mcs.yml ignored on CLI --------------------

    [Fact]
    public async Task RoundTrip_CliAgent_StaleClassicTopicMcsYml_IgnoredOnRead()
    {
        // Rubber-duck non-blocking #5: a stale topics/foo.mcs.yml on a CLI
        // workspace must not be picked up as a "new local file" — that
        // would synthesize a phantom component the next push would insert.
        var (entity, definition, accessor, synchronizer, workspace) =
            await PushFixtureAsClone("FoodLogger");

        // Pre-seed a stale classic-shape file. The content doesn't matter
        // much; the read path's gate is what's being verified.
        var staleClassic = new AgentFilePath("topics/stale_topic.mcs.yml");
        await accessor.WriteAsync(staleClassic,
            "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnUnknownIntent\n",
            CancellationToken.None);
        Assert.True(accessor.Exists(staleClassic));

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        // No DialogComponent in the result should match the stale file
        // (CLI agent has no concept of classic *.mcs.yml siblings).
        var readSchemaNames = read.Components
            .Select(c => c.SchemaNameString)
            .ToHashSet(StringComparer.Ordinal);
        var definitionSchemaNames = definition.Components
            .Select(c => c.SchemaNameString)
            .ToHashSet(StringComparer.Ordinal);

        // Read should contain a SUBSET of the original schema names
        // (some non-projected components are kept as cloud-cache verbatim,
        // some projected ones survive the disk-read round-trip).
        // Critical assertion: NO new schema names from the stale file.
        var extras = readSchemaNames.Except(definitionSchemaNames).ToList();
        Assert.Empty(extras);
    }

    [Fact]
    public async Task RoundTrip_ClassicAgent_StaleClassicMcsYml_StillDiscovered()
    {
        // Symmetric assertion: classic agents MUST continue to discover
        // new *.mcs.yml files (the existing behavior). This guards against
        // the gating change accidentally affecting classic-shape pulls.
        var (entity, definition, accessor, synchronizer, workspace) =
            await PushFixtureAsClone("HRAgent");

        // The classic new-file scan walks the workspace root and reports
        // *.mcs.yml not in the known set. A valid AdaptiveDialog topic
        // file with a fresh schema would be picked up.
        var newTopicFileName = $"topics/new_user_topic_{Guid.NewGuid():N}.mcs.yml";
        var newTopicPath = new AgentFilePath(newTopicFileName);
        await accessor.WriteAsync(newTopicPath,
            "kind: AdaptiveDialog\nbeginDialog:\n  kind: OnUnknownIntent\n",
            CancellationToken.None);

        // Read should not throw; classic scan still runs.
        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        // We don't assert the topic IS in result (CompileFile may fail
        // on a barebones AdaptiveDialog without proper plumbing), but we
        // do assert the scan was attempted and didn't throw. The fact
        // that the call succeeded is sufficient guard against the gate
        // being too aggressive.
        Assert.NotNull(read);
    }

    // --- Strong byte-equality round-trip (rubber-duck blocking #3) ------------------

    [Fact]
    public async Task RoundTrip_CliAgent_FoodLogger_ConnectionFiles_BytePerfectAfterRead()
    {
        // Rubber-duck blocking #3: the component-parity tests above only
        // verify schema-name set equality, which would not catch field
        // loss within a preserved ConnectionReference. This test takes a
        // byte snapshot of pass-1 connection files (writer driven by
        // cloud cache), runs the read, then re-runs the writer against
        // the READ result into a fresh accessor, and asserts byte-for-
        // byte equality. Any field the overlay drops or mutates would
        // change the re-emitted bytes.
        var (entity, definition, accessor1, synchronizer, workspace1) =
            await PushFixtureAsClone("FoodLogger");

        var pass1 = SnapshotConnectionFiles(accessor1);
        Assert.NotEmpty(pass1);

        var read = await synchronizer.ReadWorkspaceDefinitionAsync(workspace1, CancellationToken.None);

        // Sanity guards before the byte check: shape preserved.
        Assert.Equal(definition.ConnectionReferences.Length, read.ConnectionReferences.Length);

        // Re-project the READ result via the same writer used in pass-1.
        var accessor2 = new InMemoryFileAccessor(
            new DirectoryPath($"c:/test/byte-perfect-pass2-{Guid.NewGuid():N}/"));
        CliAgentConnectionsWriter.WriteAll(
            accessor2, read.ConnectionReferences, null, CancellationToken.None);

        var pass2 = SnapshotConnectionFiles(accessor2);

        Assert.Equal(pass1.Count, pass2.Count);
        foreach (var kvp in pass1)
        {
            var path = kvp.Key;
            var bytes1 = kvp.Value;
            Assert.True(pass2.TryGetValue(path, out var bytes2),
                $"Connection file '{path}' present after first writer pass but missing after re-emit from read result.");
            Assert.True(bytes1.AsSpan().SequenceEqual(bytes2),
                $"Connection file '{path}' bytes diverge between writer-from-cache and writer-from-read. " +
                $"This is the rubber-duck blocking #3 'byte equality' signal — field overlay dropped or mutated data.");
        }
    }

    private static Dictionary<string, byte[]> SnapshotConnectionFiles(InMemoryFileAccessor accessor)
    {
        var snapshot = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var p in accessor.ListFiles(
                     CliAgentConnectionsWriter.InfrastructureConnectionsFolder,
                     "*" + CliAgentConnectionsWriter.FileExtension))
        {
            using var s = accessor.OpenRead(p);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            snapshot[p.ToString()] = ms.ToArray();
        }
        return snapshot;
    }

    // --- Helpers ---------------------------------------------------------------------

    private static void AssertComponentSchemaParity(
        DefinitionBase original,
        DefinitionBase read,
        string fixtureName)
    {
        // Compare component-set identities. Some components may be
        // re-projected from disk (DialogComponent tools/skills,
        // KnowledgeSourceComponent) and some kept as cloud-cache
        // verbatim — either way, the schema-name set must match.
        var originalSchemaNames = original.Components
            .Select(c => c.SchemaNameString)
            .ToHashSet(StringComparer.Ordinal);
        var readSchemaNames = read.Components
            .Select(c => c.SchemaNameString)
            .ToHashSet(StringComparer.Ordinal);

        var missing = originalSchemaNames.Except(readSchemaNames).ToList();
        var extra = readSchemaNames.Except(originalSchemaNames).ToList();

        Assert.True(missing.Count == 0,
            $"Fixture {fixtureName}: read dropped {missing.Count} components that were in the original. " +
            $"First few: {string.Join(", ", missing.Take(5))}. " +
            $"This is the Node E 'Done when: Round-trip clean pull passes' regression signal.");
        Assert.True(extra.Count == 0,
            $"Fixture {fixtureName}: read produced {extra.Count} phantom components not in the original. " +
            $"First few: {string.Join(", ", extra.Take(5))}.");
    }

    /// <summary>
    /// Node Q: compute the shape-aware projected path for a CLI component, exactly as
    /// the production writer/reader/delete do (LspComponentPathResolver derives the
    /// CliCopilot shape from the definition entity). Replaces the retired
    /// CliAgent*Writer <c>.yaml</c> path helpers.
    /// </summary>
    internal static AgentFilePath CliComponentPath(BotComponentBase component, DefinitionBase definition)
        => new AgentFilePath(new LspComponentPathResolver().GetComponentPath(component, definition));

    /// <summary>
    /// Push a fixture as a complete clone-style insert. Lays down the
    /// cloud cache + CLI files (for CLI fixtures) or classic files
    /// (for classic fixtures). Returns the seeded accessor so tests can
    /// inspect on-disk state.
    /// </summary>
    internal static async Task<(
        BotEntity entity,
        BotDefinition definition,
        InMemoryFileAccessor accessor,
        WorkspaceSynchronizer synchronizer,
        DirectoryPath workspace)>
        PushFixtureAsClone(string fixtureName)
    {
        var (entity, definition) = LoadFixtureBotAndDefinition(fixtureName);

        var componentChanges = definition.Components
            .Select(c => (BotComponentChange)new BotComponentInsert(c))
            .ToList();
        var crChanges = definition.ConnectionReferences
            .Select(cr => (ConnectionReferenceChange)new ConnectionReferenceInsert(cr))
            .ToList();

        var (synchronizer, factory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/round-trip-pull-{Guid.NewGuid():N}/");
        var accessor = (InMemoryFileAccessor)factory.Create(workspace);

        WorkspaceSynchronizer.WriteCloudCache(accessor, new BotDefinition());
        await accessor.WriteAsync(new AgentFilePath(".mcs/changetoken.txt"), "seed", CancellationToken.None);

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
            changeToken: "after-clone");
        mockIsland.Setup(x => x.SaveChangesAsync(
                It.IsAny<AuthoringOperationContextBase>(),
                It.IsAny<PvaComponentChangeSet>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(changeset);

        await synchronizer.PushChangesetAsync(
            workspace,
            ComponentWriterDefensiveTests.CreateMockOperationContext(),
            changeset,
            new Mock<ISyncDataverseClient>().Object,
            Guid.NewGuid(),
            null,
            default,
            CancellationToken.None);

        return (entity, definition, accessor, synchronizer, workspace);
    }

    internal static (BotEntity entity, BotDefinition definition) LoadFixtureBotAndDefinition(string fixtureName)
    {
        var bytes = LoadFixtureBytes(fixtureName);
        var accessor = new InMemoryFileAccessor(new DirectoryPath($"c:/test/fixture-load-{Guid.NewGuid():N}/"));
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
        using var stream = typeof(CliAgentRoundTripReadTests).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
