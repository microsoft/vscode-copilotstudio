// Copyright (C) Microsoft Corporation. All rights reserved.
//
// Node Q (operator hard-require) — the five invariants that pass a naive green suite
// but fail at the Maker if convergence is wrong:
//   1. Path agreement: write path == read/delete-scan path == shape-aware resolver path.
//   2. Body round-trip fidelity: a CLI component's $kind survives write->read (no wipe).
//   3. Delete-safety (both directions): existing .mcs.yml is NOT a delete; a removed one IS.
//   4. Old-layout-no-nuke: reading a legacy .yaml workspace with new code preserves it.
//   5. Classic stays byte-identical: classic component projection is unchanged.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentNodeQInvariantTests
{
    private static readonly string[] CliComponentFolders =
        { "behaviors/", "capabilities/tools/", "capabilities/knowledge/" };

    // --- 1. Path agreement ----------------------------------------------------------

    [Fact]
    public async Task PathAgreement_CliComponents_WriteAtShapeAwareResolverPath_AsMcsYml()
    {
        var (entity, definition, accessor, _, _) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var skills = definition.Components.OfType<DialogComponent>()
            .Where(d => d.SchemaName.Value!.IndexOf(".skill.", StringComparison.Ordinal) >= 0).ToList();
        var tools = definition.Components.OfType<DialogComponent>()
            .Where(d => d.SchemaName.Value!.IndexOf(".tool.", StringComparison.Ordinal) >= 0).ToList();
        var knowledge = definition.Components.OfType<KnowledgeSourceComponent>().ToList();

        Assert.NotEmpty(skills);
        Assert.NotEmpty(tools);
        Assert.NotEmpty(knowledge);

        foreach (var component in skills.Cast<BotComponentBase>().Concat(tools).Concat(knowledge))
        {
            var path = CliAgentRoundTripReadTests.CliComponentPath(component, definition);
            Assert.EndsWith(".mcs.yml", path.ToString(), StringComparison.Ordinal);
            Assert.True(accessor.Exists(path),
                $"Writer must have written '{component.SchemaNameString}' at the shape-aware resolver path '{path}'.");
        }

        // No legacy bare .yaml component bodies remain in the three-layer folders.
        foreach (var folder in CliComponentFolders)
        {
            foreach (var file in accessor.ListFiles(folder.TrimEnd('/'), "*.yaml"))
            {
                var leaf = file.ToString();
                Assert.True(leaf.EndsWith(".mcs.yaml", StringComparison.Ordinal)
                            || leaf.EndsWith(".sync.yaml", StringComparison.Ordinal),
                    $"Unexpected legacy bare .yaml component body after convergence: '{leaf}'.");
            }
        }
    }

    // --- 2. Body round-trip fidelity ($kind survives, no instruction/collection wipe) -

    [Fact]
    public async Task BodyRoundTrip_CliTool_KindSurvivesWriteThenRead()
    {
        var (entity, definition, accessor, sync, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var originalTools = definition.Components.OfType<DialogComponent>()
            .Where(d => d.SchemaName.Value!.IndexOf(".tool.", StringComparison.Ordinal) >= 0
                        && d.Dialog != null)
            .ToList();
        Assert.NotEmpty(originalTools);

        foreach (var original in originalTools)
        {
            var roundTripped = read.Components.OfType<DialogComponent>()
                .Single(d => d.SchemaNameString == original.SchemaNameString);

            // The discriminatable body kind ($kind) must survive the write->read; a
            // wipe would collapse it to a base/empty type (the Node-L corruption).
            Assert.Equal(original.Dialog!.GetType(), roundTripped.Dialog?.GetType());
        }
    }

    // --- 3. Delete-safety (both directions) -----------------------------------------

    [Fact]
    public async Task DeleteSafety_ExistingMcsYml_NotRead_AsDelete_RemovedOne_IsRead_AsDelete()
    {
        var (entity, definition, accessor, sync, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        var tool = definition.Components.OfType<DialogComponent>()
            .First(d => d.SchemaName.Value!.IndexOf(".tool.", StringComparison.Ordinal) >= 0
                        && d.SchemaName.Value!.IndexOf(".tool.connected-agent.", StringComparison.Ordinal) < 0);
        var toolPath = CliAgentRoundTripReadTests.CliComponentPath(tool, definition);

        // Direction A: file present -> component survives (no phantom delete).
        Assert.True(accessor.Exists(toolPath));
        var readPresent = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        Assert.Contains(readPresent.Components, c => c.SchemaNameString == tool.SchemaNameString);

        // Direction B: file removed -> component dropped (genuine delete detected).
        accessor.Delete(toolPath);
        var readDeleted = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        Assert.DoesNotContain(readDeleted.Components, c => c.SchemaNameString == tool.SchemaNameString);
    }

    // --- 4. Old-layout-no-nuke ------------------------------------------------------

    [Fact]
    public async Task OldLayout_LegacyYamlComponents_NewReader_DoesNotNuke()
    {
        var (entity, definition, accessor, sync, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");

        // Simulate a workspace cloned by PRE-Q code: components live as bare .yaml in
        // the three-layer folders, with NO .mcs.yml component bodies. settings.mcs.yml
        // (the entity) is present either way (Node P).
        foreach (var folder in CliComponentFolders)
        {
            foreach (var file in accessor.ListFiles(folder.TrimEnd('/'), "*.mcs.yml").ToList())
            {
                accessor.Delete(file);
            }
        }
        await accessor.WriteAsync(
            new AgentFilePath("capabilities/tools/legacy_tool.yaml"),
            "kind: McpTool\nserverUrl: https://example/mcp\n",
            CancellationToken.None);

        var read = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        // Every cloud-cache component must be PRESERVED (no spurious delete). The new
        // reader finds no .mcs.yml but must not interpret that as "all deleted".
        var originalSchemas = definition.Components.Select(c => c.SchemaNameString).ToHashSet(StringComparer.Ordinal);
        var readSchemas = read.Components.Select(c => c.SchemaNameString).ToHashSet(StringComparer.Ordinal);
        var nuked = originalSchemas.Except(readSchemas).ToList();
        Assert.True(nuked.Count == 0,
            $"Old-layout workspace nuked {nuked.Count} components: {string.Join(", ", nuked.Take(5))}.");
    }

    // --- 5. Classic stays byte-identical (projection unchanged) ---------------------

    [Fact]
    public async Task Classic_NoThreeLayerFolders_AndShapeGateIsInert()
    {
        var (entity, definition, accessor, _, _) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("HRAgent");

        // Classic agents never use the CLI three-layer folders.
        var allFiles = accessor.ListFiles().Select(p => p.ToString()).ToList();
        Assert.DoesNotContain(allFiles, f =>
            f.StartsWith("behaviors/", StringComparison.Ordinal)
            || f.StartsWith("capabilities/", StringComparison.Ordinal));

        // The shape gate is inert for classic: the derive-shape overload resolves to the
        // SAME path as the explicit Classic overload for every classic component.
        var resolver = new LspComponentPathResolver();
        foreach (var component in definition.Components)
        {
            Assert.Equal(
                resolver.GetComponentPath(component, definition, AuthoringShape.Classic),
                resolver.GetComponentPath(component, definition));
        }
    }
}
