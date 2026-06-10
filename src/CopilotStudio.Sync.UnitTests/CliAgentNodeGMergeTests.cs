// Copyright (C) Microsoft Corporation. All rights reserved.
//
// CliAgentSyncSupport / Node G — conflict detection and 3-way merge for the
// CLI agent layered shape.
//
// Key fact this node verifies: the existing 3-way merge
// (WorkspaceSynchronizer.ApplyThreeWayMerge → MergeComponent → MergeStrings,
// backed by the OM MergeFinder/MergeOutput line-merge) is IDENTITY (schema
// name) based and path-agnostic. The CLI layered files (agent.yaml,
// capabilities/tools, capabilities/knowledge, behaviors, infrastructure/
// connections) were already read back into schema-name-keyed components by the
// Node E/F readers, so the layered layout flows through the same merge the
// classic shape uses — no path-aware grouping change is required.
//
// Coverage:
//   - Per-file-type merge of the real FoodLogger D1-D5 body shapes
//     (disjoint edits auto-merge; same-subtree edits surface the classic
//     git-marker conflict).
//   - ApplyThreeWayMerge orchestration: both-changed resolves without data
//     loss; only-remote fast-forwards; only-local is preserved; the bot-entity
//     (agent.yaml) conflict path merges disjoint Configuration edits.
//   - Headline edit-locally-AND-remotely pull over a real CLI workspace on
//     disk resolves without data loss.
//   - D5 connection references: locked parity behavior — NOT 3-way merged
//     (remote/cloud authoritative on pull), matching the classic shape.
//   - Classic regression: a classic component still 3-way merges after the
//     seam extraction.

using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class CliAgentNodeGMergeTests
{
    private static readonly AgentFilePath CachePath = new AgentFilePath(".mcs/botdefinition.json");

    // Real FoodLogger body shapes (one per D-node serialization route).
    private const string McpToolBody =
        "kind: McpTool\n" +
        "connectorId: /providers/Microsoft.PowerApps/apis/shared_workiqsharepoint\n" +
        "connectionReference: Default_draft_rxzs_q.cr.shared_workiqsharepoint.020c62e5181149bc8c90e45269f66dce\n" +
        "operationId: mcp_SharePointRemoteServer\n";

    private const string KnowledgeBody =
        "kind: KnowledgeSourceConfiguration\n" +
        "source:\n" +
        "  kind: WebsiteKnowledgeSource\n" +
        "  siteUrl: https://en.wikipedia.org/wiki/Sodium\n";

    private readonly ITestOutputHelper _output;

    public CliAgentNodeGMergeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // --- Per-file-type merge of the line-merge primitive (MergeStrings) -------------
    // "3-way merge fixture passes per file type" — exercises the OM
    // MergeFinder/MergeOutput engine on each real CLI body shape.

    [Fact]
    public void Merge_ToolFile_McpTool_DisjointEdits_AutoMerges()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var local = McpToolBody.Replace(
            "operationId: mcp_SharePointRemoteServer",
            "operationId: mcp_LOCAL_EDIT");
        var remote = McpToolBody.Replace(
            "shared_workiqsharepoint\n",
            "shared_REMOTE_EDIT\n");

        var merged = sync.MergeStrings(McpToolBody, local, remote);

        Assert.Contains("operationId: mcp_LOCAL_EDIT", merged);
        Assert.Contains("shared_REMOTE_EDIT", merged);
        Assert.DoesNotContain("<<<<<<<", merged);
    }

    [Fact]
    public void Merge_SkillFile_DisjointEdits_DifferentSubtrees_AutoMerges()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        // InlineAgentSkill projects a multi-line literal `content` block.
        var baseBody =
            "kind: InlineAgentSkill\n" +
            "content: |+\n" +
            "  ---\n" +
            "  name: log-meal\n" +
            "  description: Estimate calories and sodium.\n" +
            "  ---\n" +
            "  Steps:\n" +
            "  1. Determine ingredients.\n" +
            "  2. Look up nutrition.\n";
        var local = baseBody.Replace(
            "  description: Estimate calories and sodium.",
            "  description: LOCAL estimate calories and sodium.");
        var remote = baseBody.Replace(
            "  2. Look up nutrition.",
            "  2. Look up REMOTE nutrition.");

        var merged = sync.MergeStrings(baseBody, local, remote);

        Assert.Contains("LOCAL estimate calories", merged);
        Assert.Contains("Look up REMOTE nutrition", merged);
        Assert.DoesNotContain("<<<<<<<", merged);
    }

    [Fact]
    public void Merge_KnowledgeFile_DisjointEdits_AutoMerges()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var local = KnowledgeBody.Replace(
            "  siteUrl: https://en.wikipedia.org/wiki/Sodium",
            "  siteUrl: https://en.wikipedia.org/wiki/Sodium_LOCAL");
        var remote = KnowledgeBody.Replace(
            "kind: KnowledgeSourceConfiguration",
            "kind: KnowledgeSourceConfiguration # remote-edit");

        var merged = sync.MergeStrings(KnowledgeBody, local, remote);

        Assert.Contains("Sodium_LOCAL", merged);
        Assert.Contains("# remote-edit", merged);
        Assert.DoesNotContain("<<<<<<<", merged);
    }

    [Fact]
    public void Merge_AgentSettings_DisjointEdits_AutoMerges()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        // agent.yaml settings shape: identity + agentSettings subtree. The two
        // edited keys are separated by an unchanged anchor line so the line
        // merge resolves them as independent hunks.
        var baseBody =
            "schemaName: test_cliagent\n" +
            "agentSettings:\n" +
            "  instructions: BASE_INSTR\n" +
            "  web: false\n" +
            "  model: BASE_MODEL\n";
        var local = baseBody.Replace("instructions: BASE_INSTR", "instructions: LOCAL_INSTR");
        var remote = baseBody.Replace("model: BASE_MODEL", "model: REMOTE_MODEL");

        var merged = sync.MergeStrings(baseBody, local, remote);

        Assert.Contains("instructions: LOCAL_INSTR", merged);
        Assert.Contains("model: REMOTE_MODEL", merged);
        Assert.DoesNotContain("<<<<<<<", merged);
    }

    // --- Same-subtree conflict: lock the classic git-marker behaviour ---------------

    [Fact]
    public void Merge_KnowledgeFile_SameSubtreeEdit_RaisesGitConflictMarkers()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var local = KnowledgeBody.Replace("Sodium\n", "Sodium_LOCAL\n");
        var remote = KnowledgeBody.Replace("Sodium\n", "Sodium_REMOTE\n");

        var merged = sync.MergeStrings(KnowledgeBody, local, remote);

        // The classic strategy surfaces a true conflict as git-style markers in
        // the merged text (MergeOutput). Node G mirrors this — no out-of-band
        // ConflictRecord is introduced.
        Assert.Contains("<<<<<<<", merged);
        Assert.Contains(">>>>>>>", merged);
    }

    // --- MergeComponent: full recompile of a CLI-shaped component --------------------

    [Fact]
    public void MergeComponent_ToolFile_DisjointEdits_RecompilesWithBothEdits()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        const string schema = "Default_draft_rxzs_q.tool.WorkIQSharePointPreview-WorkIQSharePointPreview";
        var baseComp = MakeDialogComponent(schema, McpToolBody);
        var localComp = MakeDialogComponent(schema,
            McpToolBody.Replace("operationId: mcp_SharePointRemoteServer", "operationId: mcp_LOCAL_EDIT"));
        var remoteComp = MakeDialogComponent(schema,
            McpToolBody.Replace("shared_workiqsharepoint\n", "shared_REMOTE_EDIT\n"));

        var merged = sync.MergeComponent(schema, baseComp, localComp, remoteComp);

        Assert.NotNull(merged);
        var mergedYaml = SerializeRoot(merged!);
        Assert.Contains("mcp_LOCAL_EDIT", mergedYaml);
        Assert.Contains("shared_REMOTE_EDIT", mergedYaml);
        Assert.DoesNotContain("<<<<<<<", mergedYaml);
    }

    [Fact]
    public void MergeComponent_ToolFile_SameSubtreeConflict_NeverSilentlyResolvesToOneSide()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        const string schema = "Default_draft_rxzs_q.tool.WorkIQSharePointPreview-WorkIQSharePointPreview";
        var baseComp = MakeDialogComponent(schema, McpToolBody);
        var localComp = MakeDialogComponent(schema,
            McpToolBody.Replace("operationId: mcp_SharePointRemoteServer", "operationId: mcp_LOCAL"));
        var remoteComp = MakeDialogComponent(schema,
            McpToolBody.Replace("operationId: mcp_SharePointRemoteServer", "operationId: mcp_REMOTE"));

        // Lock the classic conflict behaviour end-to-end: when local and remote
        // edit the SAME line differently, the merge MUST NOT silently collapse
        // to one side. The settled classic strategy surfaces git-style markers
        // in the merged body (MergeStrings); depending on the body's parser
        // tolerance the subsequent recompile may also throw. Either outcome is
        // acceptable — both keep the conflict visible. (Mirror-classic: Node G
        // does not introduce an out-of-band ConflictRecord.)
        var threw = false;
        string body = string.Empty;
        try
        {
            var merged = sync.MergeComponent(schema, baseComp, localComp, remoteComp);
            body = SerializeRoot(merged!);
        }
        catch (Exception ex)
        {
            threw = true;
            _output.WriteLine($"MergeComponent threw on same-subtree conflict: {ex.GetType().Name}: {ex.Message}");
        }

        Assert.True(
            threw || body.Contains("<<<<<<<"),
            "A true same-subtree conflict must remain visible (git markers in the body) " +
            "or fail the recompile — it must never be silently resolved to one side.");
    }

    // --- ApplyThreeWayMerge orchestration -------------------------------------------

    [Fact]
    public void ApplyThreeWayMerge_CliComponent_BothChanged_MergesWithoutDataLoss()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        const string schema = "Default_draft_rxzs_q.tool.WorkIQSharePointPreview-WorkIQSharePointPreview";
        var baseComp = MakeDialogComponent(schema, McpToolBody);
        var cloud = new BotDefinition().WithComponents(new[] { baseComp });

        var localComp = MakeDialogComponent(schema,
            McpToolBody.Replace("operationId: mcp_SharePointRemoteServer", "operationId: mcp_LOCAL_EDIT"));
        var remoteComp = MakeDialogComponent(schema,
            McpToolBody.Replace("shared_workiqsharepoint\n", "shared_REMOTE_EDIT\n"));

        var localChanges = MakeUpdateChanges(localComp);
        var remoteChanges = MakeUpdateChanges(remoteComp);

        var merged = sync.ApplyThreeWayMerge(localChanges, remoteChanges, cloud);

        var mergedComp = merged.BotComponentChanges.OfType<BotComponentUpsert>()
            .Single(c => c.Component?.SchemaNameString == schema).Component!;
        var mergedYaml = SerializeRoot(mergedComp);
        Assert.Contains("mcp_LOCAL_EDIT", mergedYaml);
        Assert.Contains("shared_REMOTE_EDIT", mergedYaml);
    }

    [Fact]
    public void ApplyThreeWayMerge_OnlyRemoteChanged_FastForwards()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        const string schema = "Default_draft_rxzs_q.tool.WorkIQSharePointPreview-WorkIQSharePointPreview";
        var baseComp = MakeDialogComponent(schema, McpToolBody);
        var cloud = new BotDefinition().WithComponents(new[] { baseComp });

        var remoteComp = MakeDialogComponent(schema,
            McpToolBody.Replace("operationId: mcp_SharePointRemoteServer", "operationId: mcp_REMOTE_ONLY"));

        // No local change for this schema → no conflict → remote passes through.
        var localChanges = (
            new PvaComponentChangeSet(new List<BotComponentChange>(), bot: null, changeToken: "t"),
            ImmutableArray<Change>.Empty);
        var remoteChanges = MakeUpdateChanges(remoteComp);

        var merged = sync.ApplyThreeWayMerge(localChanges, remoteChanges, cloud);

        var mergedComp = merged.BotComponentChanges.OfType<BotComponentUpsert>()
            .Single(c => c.Component?.SchemaNameString == schema).Component!;
        Assert.Contains("mcp_REMOTE_ONLY", SerializeRoot(mergedComp));
    }

    [Fact]
    public void ApplyThreeWayMerge_OnlyLocalChanged_PreservesLocal()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        const string schema = "Default_draft_rxzs_q.tool.WorkIQSharePointPreview-WorkIQSharePointPreview";
        var baseComp = MakeDialogComponent(schema, McpToolBody);
        var cloud = new BotDefinition().WithComponents(new[] { baseComp });

        var localComp = MakeDialogComponent(schema,
            McpToolBody.Replace("operationId: mcp_SharePointRemoteServer", "operationId: mcp_LOCAL_ONLY"));

        var localChanges = MakeUpdateChanges(localComp);
        // Remote unchanged for this schema → no conflict; merged set must not
        // clobber the local edit back to the base/cloud value.
        var remoteChanges = (
            new PvaComponentChangeSet(new List<BotComponentChange>(), bot: null, changeToken: "t"),
            ImmutableArray<Change>.Empty);

        var merged = sync.ApplyThreeWayMerge(localChanges, remoteChanges, cloud);

        Assert.DoesNotContain(
            merged.BotComponentChanges.OfType<BotComponentUpsert>(),
            c => c.Component?.SchemaNameString == schema
                 && SerializeRoot(c.Component!).Contains("mcp_SharePointRemoteServer"));
    }

    [Fact]
    public void ApplyThreeWayMerge_AgentYaml_DisjointConfigEdits_KeepsBoth()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        // D1 / agent.yaml path: the bot-entity conflict merge runs on the
        // settings YAML (WithOnlySettingsYamlProperties, which preserves the
        // Configuration JsonString holding recognizer + agentSettings). Local
        // edits the model series; remote edits the instructions text — disjoint
        // subtrees that must both survive.
        var cloud = MakeCliBotDefinition(version: 100, instructions: "BASE_INSTR", series: "BASE_SERIES");
        var local = MakeCliBotEntity(version: 100, instructions: "BASE_INSTR", series: "LOCAL_SERIES");
        var remote = MakeCliBotEntity(version: 101, instructions: "REMOTE_INSTR", series: "BASE_SERIES");

        var localChanges = (
            new PvaComponentChangeSet(new List<BotComponentChange>(), bot: local, changeToken: "t"),
            ImmutableArray<Change>.Empty);
        var remoteChanges = (
            new PvaComponentChangeSet(new List<BotComponentChange>(), bot: remote, changeToken: "t"),
            ImmutableArray<Change>.Empty);

        var merged = sync.ApplyThreeWayMerge(localChanges, remoteChanges, cloud);

        Assert.NotNull(merged.Bot);
        var mergedEntityYaml = CodeSerializer.Serialize(merged.Bot!);
        Assert.Contains("REMOTE_INSTR", mergedEntityYaml);
        Assert.Contains("LOCAL_SERIES", mergedEntityYaml);
    }

    // --- Headline: edit a CLI workspace ON DISK locally AND remotely -----------------

    [Fact]
    public async Task Pull_CliAgent_LocalAndRemoteBothChanged_ResolvesWithoutDataLoss()
    {
        var (entity, definition, accessor, sync, workspace) =
            await CliAgentRoundTripReadTests.PushFixtureAsClone("FoodLogger");
        var cloud = WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;

        var tool = definition.Components.OfType<DialogComponent>()
            .Single(d => d.SchemaName.Value!.Contains("WorkIQSharePointPreview"));
        var toolPath = CliAgentRoundTripReadTests.CliComponentPath(tool, definition);
        var body = ReadAll(accessor, toolPath);

        // LOCAL edit on the layered tool file (operationId).
        var localBody = body.Replace("operationId: mcp_SharePointRemoteServer", "operationId: mcp_LOCAL");
        await accessor.WriteAsync(toolPath, localBody, CancellationToken.None);
        var local = await sync.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);
        var localChanges = sync.GetLocalChanges(local, cloud, accessor, "t");

        // REMOTE edit on a DIFFERENT line (connectorId), carrying cloud Id/Version.
        // Anchor on `apis/shared_workiqsharepoint` (unique to the connectorId line;
        // the connectionReference line carries `cr.shared_workiqsharepoint`) so the
        // edit lands regardless of the file's CRLF line endings.
        var remoteTool = MakeDialogComponent(
            tool.SchemaName.Value!,
            body.Replace("apis/shared_workiqsharepoint", "apis/shared_REMOTE"),
            tool.Id.Value,
            tool.Version);
        var remoteChanges = (
            new PvaComponentChangeSet(
                new List<BotComponentChange> { new BotComponentUpdate(remoteTool) },
                bot: null, changeToken: "t"),
            ImmutableArray.Create(new Change
            {
                ChangeType = ChangeType.Update,
                Name = tool.SchemaName.Value!,
                Uri = toolPath.ToString(),
                SchemaName = tool.SchemaName.Value!,
                ChangeKind = tool.Kind.ToString(),
            }));

        var merged = sync.ApplyThreeWayMerge(localChanges, remoteChanges, cloud);

        var mergedComp = merged.BotComponentChanges.OfType<BotComponentUpsert>()
            .Single(c => c.Component?.SchemaNameString == tool.SchemaName.Value).Component!;
        var mergedYaml = SerializeRoot(mergedComp);
        Assert.Contains("operationId: mcp_LOCAL", mergedYaml);
        Assert.Contains("apis/shared_REMOTE", mergedYaml);
        Assert.DoesNotContain("<<<<<<<", mergedYaml);
    }

    // --- Classic regression: the merge engine is unchanged for classic shapes -------
    // The seam extraction's behaviour-preservation for the orchestration path is
    // guarded by the full pre-existing suite (222/222). This test additionally
    // proves the line-merge engine still resolves a classic topic (AdaptiveDialog)
    // YAML body — the same primitive the CLI routes use.

    [Fact]
    public void Merge_ClassicTopicBody_DisjointEdits_AutoMerges()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        var baseBody =
            "kind: AdaptiveDialog\n" +
            "beginDialog:\n" +
            "  kind: OnConversationStart\n" +
            "  actions:\n" +
            "    - kind: SendActivity\n" +
            "      activity: BASE_GREETING\n" +
            "    - kind: SendActivity\n" +
            "      activity: BASE_FAREWELL\n";
        var local = baseBody.Replace("activity: BASE_GREETING", "activity: LOCAL_GREETING");
        var remote = baseBody.Replace("activity: BASE_FAREWELL", "activity: REMOTE_FAREWELL");

        var merged = sync.MergeStrings(baseBody, local, remote);

        Assert.Contains("activity: LOCAL_GREETING", merged);
        Assert.Contains("activity: REMOTE_FAREWELL", merged);
        Assert.DoesNotContain("<<<<<<<", merged);
    }

    // --- D5 connection references: locked parity (NOT 3-way merged) -----------------

    [Fact]
    public void ApplyThreeWayMerge_CliConnectionRef_BothChanged_RemoteWins_NotMerged()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        const string logicalName = "shared_demo_cr";
        var cloud = new BotDefinition();

        var localCr = MakeConnectionRef(logicalName, "/providers/.../shared_LOCAL");
        var remoteCr = MakeConnectionRef(logicalName, "/providers/.../shared_REMOTE");

        var crChange = new Change
        {
            ChangeType = ChangeType.Update,
            Name = logicalName,
            Uri = $"infrastructure/connections/{logicalName}{CliAgentConnectionsWriter.FileExtension}",
            SchemaName = logicalName,
            ChangeKind = nameof(ConnectionReference),
        };

        var localChanges = (
            new PvaComponentChangeSet(
                botComponentChanges: new List<BotComponentChange>(),
                connectorDefinitionChanges: null,
                environmentVariableChanges: null,
                connectionReferenceChanges: new List<ConnectionReferenceChange> { new ConnectionReferenceUpdate(localCr) },
                aIPluginOperationChanges: null,
                componentCollectionChanges: null,
                dataverseTableSearchChanges: null,
                dataverseTableSearchEntityConfigurationChanges: null,
                connectedAgentDefinitionChanges: null,
                bot: null,
                changeToken: "t"),
            ImmutableArray.Create(crChange));

        var remoteChanges = (
            new PvaComponentChangeSet(
                botComponentChanges: new List<BotComponentChange>(),
                connectorDefinitionChanges: null,
                environmentVariableChanges: null,
                connectionReferenceChanges: new List<ConnectionReferenceChange> { new ConnectionReferenceUpdate(remoteCr) },
                aIPluginOperationChanges: null,
                componentCollectionChanges: null,
                dataverseTableSearchChanges: null,
                dataverseTableSearchEntityConfigurationChanges: null,
                connectedAgentDefinitionChanges: null,
                bot: null,
                changeToken: "t"),
            ImmutableArray.Create(crChange));

        var merged = sync.ApplyThreeWayMerge(localChanges, remoteChanges, cloud);

        // Locked behaviour: CRs are not 3-way merged. The remote changeset's CR
        // entry passes through unchanged (remote/cloud authoritative on pull),
        // matching the classic shape. The local edit is NOT applied.
        var update = Assert.Single(merged.ConnectionReferenceChanges.OfType<ConnectionReferenceUpdate>());
        Assert.Equal("/providers/.../shared_REMOTE", update.ConnectionReference!.ConnectorId.Value);
    }

    [Fact]
    public void ApplyThreeWayMerge_CliConnectionRef_LocalDelete_DroppedOnPull()
    {
        var (sync, _, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();

        const string logicalName = "shared_demo_cr";
        var cloud = new BotDefinition();

        // Local intends to delete the CR; remote changeset carries no CR change.
        var localChanges = (
            new PvaComponentChangeSet(
                botComponentChanges: new List<BotComponentChange>(),
                connectorDefinitionChanges: null,
                environmentVariableChanges: null,
                connectionReferenceChanges: new List<ConnectionReferenceChange> { new ConnectionReferenceDelete(Guid.NewGuid(), 1) },
                aIPluginOperationChanges: null,
                componentCollectionChanges: null,
                dataverseTableSearchChanges: null,
                dataverseTableSearchEntityConfigurationChanges: null,
                connectedAgentDefinitionChanges: null,
                bot: null,
                changeToken: "t"),
            ImmutableArray.Create(new Change
            {
                ChangeType = ChangeType.Delete,
                Name = logicalName,
                Uri = $"infrastructure/connections/{logicalName}{CliAgentConnectionsWriter.FileExtension}",
                SchemaName = logicalName,
                ChangeKind = nameof(ConnectionReference),
            }));
        var remoteChanges = (
            new PvaComponentChangeSet(new List<BotComponentChange>(), bot: null, changeToken: "t"),
            ImmutableArray<Change>.Empty);

        var merged = sync.ApplyThreeWayMerge(localChanges, remoteChanges, cloud);

        // Locked behaviour (rubber-duck distrust case): the pull merge does NOT
        // carry a local CR delete — the remote/cloud CR state is authoritative.
        // Documented D5 parity, not a regression introduced by Node G.
        Assert.Empty(merged.ConnectionReferenceChanges);
    }

    // --- Helpers --------------------------------------------------------------------

    private static (PvaComponentChangeSet, ImmutableArray<Change>) MakeUpdateChanges(BotComponentBase updated)
    {
        var changeset = new PvaComponentChangeSet(
            new List<BotComponentChange> { new BotComponentUpdate(updated) },
            bot: null,
            changeToken: "t");
        var change = new Change
        {
            ChangeType = ChangeType.Update,
            Name = updated.SchemaNameString,
            Uri = updated.SchemaNameString,
            SchemaName = updated.SchemaNameString,
            ChangeKind = updated.Kind.ToString(),
        };
        return (changeset, ImmutableArray.Create(change));
    }

    private static DialogComponent MakeDialogComponent(
        string schemaName, string dialogYaml, Guid? id = null, long version = 1)
    {
        var idValue = (id ?? Guid.Parse("00000000-0000-0000-0000-000000000001")).ToString();
        var json = $$"""
        {
          "$kind": "BotDefinition",
          "components": [
            {
              "$kind": "DialogComponent",
              "id": "{{idValue}}",
              "version": {{version}},
              "schemaName": {{System.Text.Json.JsonSerializer.Serialize(schemaName)}},
              "dialog": {{System.Text.Json.JsonSerializer.Serialize(dialogYaml)}}
            }
          ]
        }
        """;
        return ReadDefinition(json).Components.OfType<DialogComponent>().Single();
    }

    private static BotDefinition MakeCliBotDefinition(int version, string instructions, string series) =>
        new BotDefinition().WithEntity(MakeCliBotEntity(version, instructions, series));

    private static BotEntity MakeCliBotEntity(int version, string instructions, string series)
    {
        var json = $$"""
        {
          "$kind": "BotDefinition",
          "entity": {
            "$kind": "BotEntity",
            "schemaName": "test_cliagent",
            "template": "cliagent-1.0.0",
            "version": {{version}},
            "configuration": {
              "$kind": "BotConfiguration",
              "recognizer": { "$kind": "CLICopilotRecognizer" },
              "agentSettings": {
                "$kind": "AgentSettings",
                "instructions": {
                  "$kind": "Instructions",
                  "segments": [
                    { "$kind": "StaticSegment", "value": {{System.Text.Json.JsonSerializer.Serialize(instructions)}} }
                  ]
                },
                "model": { "$kind": "ModelConfig", "series": {{System.Text.Json.JsonSerializer.Serialize(series)}} },
                "web": { "$kind": "WebSettings", "enableWebSearch": false }
              }
            }
          }
        }
        """;
        return ReadDefinition(json).Entity!;
    }

    private static ConnectionReference MakeConnectionRef(string logicalName, string connectorId)
    {
        var builder = new ConnectionReference.Builder
        {
            ConnectionReferenceLogicalName = logicalName,
            ConnectorId = connectorId,
        };
        return builder.Build();
    }

    private static BotDefinition ReadDefinition(string botDefinitionJson)
    {
        var accessor = new InMemoryFileAccessor(new DirectoryPath($"c:/test/nodeg-{Guid.NewGuid():N}/"));
        var bytes = Encoding.UTF8.GetBytes(botDefinitionJson);
        using (var s = accessor.OpenWrite(CachePath))
        {
            s.Write(bytes, 0, bytes.Length);
        }
        return (BotDefinition)WorkspaceSynchronizer.ReadCloudCacheSnapshot(accessor)!;
    }

    private static string SerializeRoot(BotComponentBase component)
    {
        if (component.RootElement == null)
        {
            return string.Empty;
        }
        using var sw = new StringWriter();
        CodeSerializer.Serialize(sw, component.RootElement);
        return sw.ToString();
    }

    private static string ReadAll(InMemoryFileAccessor accessor, AgentFilePath path)
    {
        using var s = accessor.OpenRead(path);
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
