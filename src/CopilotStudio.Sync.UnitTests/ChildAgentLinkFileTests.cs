// Copyright (C) Microsoft Corporation. All rights reserved.

using System;
using System.Linq;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Covers the hidden ".agent.json" child-agent link file and how a local (push/preview)
/// diff resolves each child-agent folder to its real cloud schema - from the link when
/// present, or self-healed from the cloud cache when it is missing.
/// </summary>
public class ChildAgentLinkFileTests
{
    private const string Bot = "cre98_AgentC1";

    private static IFileAccessor CreateAccessor()
        => new InMemoryFileAccessorFactory().Create(new DirectoryPath("c:/test/workspace/"));

    // ---- ChildAgentLinkFile.WriteLink / ListFolders (file I/O) ----------------------

    [Fact]
    public void WriteLink_WritesNoBom_AndListFoldersRoundTrips()
    {
        var accessor = CreateAccessor();
        WriteAgentDefinition(accessor, "agents/Transfer Funds/agent.mcs.yml");

        ChildAgentLinkFile.WriteLink(
            accessor,
            new AgentFilePath("agents/Transfer Funds/agent.mcs.yml"),
            $"{Bot}.agent.TransferFunds");

        // JSON must not start with a UTF-8 BOM.
        using (var stream = accessor.OpenRead(new AgentFilePath("agents/Transfer Funds/.agent.json")))
        {
            Assert.Equal((byte)'{', stream.ReadByte());
        }

        var folder = Assert.Single(ChildAgentLinkFile.ListFolders(accessor));
        Assert.Equal("Transfer Funds", folder.FolderName);
        Assert.NotNull(folder.Link);
        Assert.Equal($"{Bot}.agent.TransferFunds", folder.Link!.SchemaName);
        Assert.Equal("Transfer Funds", folder.Link.FolderName);
    }

    [Fact]
    public void ListFolders_NoChildAgents_Empty()
    {
        var accessor = CreateAccessor();
        WriteText(accessor, "topics/Greeting.mcs.yml", "kind: AdaptiveDialog");

        Assert.Empty(ChildAgentLinkFile.ListFolders(accessor));
    }

    [Theory]
    [InlineData(null)]                 // link file absent
    [InlineData("{ not valid json")]   // link file malformed
    [InlineData("{ }")]                // link file present but empty (no schema/folder)
    public void ListFolders_MissingOrUnusableLink_LinkIsNull(string? linkContent)
    {
        var accessor = CreateAccessor();
        WriteAgentDefinition(accessor, "agents/Foo/agent.mcs.yml");
        if (linkContent != null)
        {
            WriteText(accessor, "agents/Foo/.agent.json", linkContent);
        }

        var folder = Assert.Single(ChildAgentLinkFile.ListFolders(accessor));
        Assert.Equal("Foo", folder.FolderName);
        Assert.Null(folder.Link);
    }

    // ---- GetLocalChanges wiring (push/preview validates link files) ------------------

    // ---- GetLocalChanges: resolve child-agent schema (link or self-heal), then remap ----

    [Fact]
    public void GetLocalChanges_ValidLink_RemapsSchema_NotFlaggedAsNew()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-remap/"));

        // On disk: a friendly folder whose link points at the real cloud (machine) schema.
        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/TransferFunds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_7_8\", \"folderName\": \"TransferFunds\" }");

        // Cloud carries the server machine schema; the LSP-compiled local derives it from the folder.
        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        AssertNoChildAgentCreateOrDelete(changes);
    }

    [Fact]
    public void GetLocalChanges_ValidLink_SchemaMissingFromCloud_Throws()
    {
        // The link points at a schema that is no longer in the cloud cache - e.g. the child
        // agent was deleted in the cloud, the workspace was reattached to a different agent, or
        // the link was corrupted. Trusting it would remap to a non-existent component and flag
        // the child agent as a spurious Create; instead we fail fast with an actionable message.
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-stale-link/"));
        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/TransferFunds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_7_8\", \"folderName\": \"TransferFunds\" }");

        // Cloud still has other child agents, but not the one the link references.
        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_Other", "Other Agent");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Agent_7_8", ex.Message);
        Assert.Contains("agents/TransferFunds", ex.Message);
        Assert.Contains("Re-clone", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_StaleLink_DoesNotSilentlySelfHealByDisplayName_Throws()
    {
        // A present-but-stale link fails fast even when the folder would otherwise self-heal by
        // display name: a broken link signals real drift (reattach/corruption) the user must
        // resolve explicitly (re-clone), so we do not silently rescue it via the self-heal path.
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-stale-link-heal/"));
        WriteAgentDefinition(fileAccessor, "agents/Transfer Funds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/Transfer Funds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_STALE\", \"folderName\": \"Transfer Funds\" }");

        // The folder "Transfer Funds" would self-heal to this cloud agent by display name if the
        // link were absent - but the stale link takes precedence and must throw.
        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Agent_STALE", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_MissingLink_SelfHealsByDisplayName_NotFlaggedAsNew()
    {
        // No .agent.json (e.g. cloned before the link file existed). The friendly folder is
        // self-healed to the cloud schema by matching the projected cloud display name.
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-heal-display/"));
        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        AssertNoChildAgentCreateOrDelete(changes);
    }

    [Fact]
    public void GetLocalChanges_MissingLink_OldCloneSchemaFolder_SelfHealsBySchema_NotFlaggedAsNew()
    {
        // Clones predating the friendly-folder projection used the machine schema as the folder
        // name; the folder-derived schema already equals the cloud schema, so self-heal correlates
        // it without a link file or a re-clone.
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-heal-schema/"));
        WriteAgentDefinition(fileAccessor, "agents/Agent_7_8/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        AssertNoChildAgentCreateOrDelete(changes);
    }

    [Fact]
    public void GetLocalChanges_MissingLink_NoCloudMatch_Throws()
    {
        // A folder that matches no cloud agent (hand-created or renamed) cannot be resolved.
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-orphan/"));
        WriteAgentDefinition(fileAccessor, "agents/Ghost/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.Ghost", "Ghost");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("Ghost", ex.Message);
        Assert.Contains("does not correspond", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_MissingLink_AmbiguousMatch_Throws()
    {
        // Two cloud agents whose display names both project to the folder "Shared" -> ambiguous.
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-ambiguous/"));
        WriteAgentDefinition(fileAccessor, "agents/Shared/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.Agent_1", "Shared"),
            ("crd1c_agent.agent.Agent_2", "Shared."));
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.Shared", "Shared");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("Shared", ex.Message);
        Assert.Contains("multiple", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_RenamedFolder_Throws()
    {
        // The link file (written at clone time) recorded "Original", but the folder is "Renamed".
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-renamed/"));
        WriteAgentDefinition(fileAccessor, "agents/Renamed/agent.mcs.yml");
        WriteText(fileAccessor, "agents/Renamed/.agent.json",
            $"{{ \"schemaName\": \"{Bot}.agent.Original\", \"folderName\": \"Original\" }}");

        var def = CreateDefinition();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(def, def, fileAccessor, "token-1"));
        Assert.Contains("'Renamed'", ex.Message);
        Assert.Contains("'Original'", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_RemotePreview_SkipsResolution()
    {
        // A missing link would fail a local diff, but remote (pull) previews rewrite local
        // folders from the cloud, so resolution is skipped and no exception is thrown.
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-remote/"));
        WriteAgentDefinition(fileAccessor, "agents/Foo/agent.mcs.yml");

        var def = CreateDefinition();

        var (_, _) = synchronizer.GetLocalChanges(def, def, fileAccessor, "token-1", isRemoteChange: true);
    }

    private static void AssertNoChildAgentCreateOrDelete(System.Collections.Generic.IEnumerable<Change> changes)
    {
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Create && c.SchemaName.Contains(".agent."));
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Delete && c.SchemaName.Contains(".agent."));
    }

    private static BotDefinition CreateDefinition()
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;
        return new BotDefinition().WithEntity(botEntity);
    }

    private static BotDefinition CreateDefinitionWithChildAgent(string agentSchema, string displayName)
        => CreateDefinitionWithChildAgents((agentSchema, displayName));

    private static BotDefinition CreateDefinitionWithChildAgents(params (string Schema, string DisplayName)[] agents)
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var components = agents.Select(a => (BotComponentBase)new DialogComponent(
            schemaName: a.Schema,
            displayName: a.DisplayName,
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog())).ToArray();
        return new BotDefinition().WithEntity(botEntity).WithComponents(components);
    }

    private static void WriteAgentDefinition(IFileAccessor accessor, string path)
        => WriteText(accessor, path, "kind: GptComponentMetadata");

    private static void WriteText(IFileAccessor accessor, string path, string contents)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(contents);
    }
}
