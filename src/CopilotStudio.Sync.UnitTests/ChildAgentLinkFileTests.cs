// Copyright (C) Microsoft Corporation. All rights reserved.

using System;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Covers the hidden ".agent.json" child-agent link file: it is written beside each
/// cloned child agent's agent.mcs.yml and validated on every local (push/preview) diff.
/// </summary>
public class ChildAgentLinkFileTests
{
    private const string Bot = "cre98_AgentC1";

    private static IFileAccessor CreateAccessor()
        => new InMemoryFileAccessorFactory().Create(new DirectoryPath("c:/test/workspace/"));

    // ---- ChildAgentLinkFile helper, direct ------------------------------------------

    [Fact]
    public void WriteLink_ThenValidateAll_RoundTrips()
    {
        var accessor = CreateAccessor();
        WriteAgentDefinition(accessor, "agents/Transfer Funds/agent.mcs.yml");

        ChildAgentLinkFile.WriteLink(
            accessor,
            new AgentFilePath("agents/Transfer Funds/agent.mcs.yml"),
            $"{Bot}.agent.TransferFunds");

        Assert.True(accessor.Exists(new AgentFilePath("agents/Transfer Funds/.agent.json")));

        // The link folderName is the on-disk folder (spaces kept); validation passes.
        ChildAgentLinkFile.ValidateAll(accessor);
    }

    [Fact]
    public void ValidateAll_NoChildAgents_DoesNotThrow()
    {
        var accessor = CreateAccessor();
        WriteText(accessor, "topics/Greeting.mcs.yml", "kind: AdaptiveDialog");

        ChildAgentLinkFile.ValidateAll(accessor);
    }

    [Fact]
    public void ValidateAll_MissingLinkFile_Throws()
    {
        var accessor = CreateAccessor();
        WriteAgentDefinition(accessor, "agents/Transfer Funds/agent.mcs.yml");

        var ex = Assert.Throws<InvalidOperationException>(() => ChildAgentLinkFile.ValidateAll(accessor));
        Assert.Contains("agents/Transfer Funds", ex.Message);
        Assert.Contains(".agent.json", ex.Message);
    }

    [Fact]
    public void ValidateAll_RenamedFolder_ThrowsWithExpectedFolderName()
    {
        var accessor = CreateAccessor();
        // The folder on disk is "Renamed", but the link file (written at clone time)
        // recorded the original "Transfer Funds" -> the user renamed the folder.
        WriteAgentDefinition(accessor, "agents/Renamed/agent.mcs.yml");
        WriteText(accessor, "agents/Renamed/.agent.json",
            $"{{ \"schemaName\": \"{Bot}.agent.TransferFunds\", \"folderName\": \"Transfer Funds\" }}");

        var ex = Assert.Throws<InvalidOperationException>(() => ChildAgentLinkFile.ValidateAll(accessor));
        Assert.Contains("'Renamed'", ex.Message);
        Assert.Contains("'Transfer Funds'", ex.Message);
    }

    [Fact]
    public void ValidateAll_MalformedLinkFile_Throws()
    {
        var accessor = CreateAccessor();
        WriteAgentDefinition(accessor, "agents/Foo/agent.mcs.yml");
        WriteText(accessor, "agents/Foo/.agent.json", "{ this is not json");

        Assert.Throws<InvalidOperationException>(() => ChildAgentLinkFile.ValidateAll(accessor));
    }

    // ---- GetLocalChanges wiring (push/preview validates link files) ------------------

    [Fact]
    public void GetLocalChanges_LocalPush_MissingLinkFile_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/ws-missing/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        WriteAgentDefinition(fileAccessor, "agents/Foo/agent.mcs.yml");

        var def = CreateDefinition();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(def, def, fileAccessor, "token-1"));
        Assert.Contains(".agent.json", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_LocalPush_RenamedFolder_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/ws-renamed/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        WriteAgentDefinition(fileAccessor, "agents/Renamed/agent.mcs.yml");
        WriteText(fileAccessor, "agents/Renamed/.agent.json",
            $"{{ \"schemaName\": \"{Bot}.agent.Original\", \"folderName\": \"Original\" }}");

        var def = CreateDefinition();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(def, def, fileAccessor, "token-1"));
        Assert.Contains("'Original'", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_LocalPush_ValidLinkFile_DoesNotThrowFromValidation()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/ws-valid/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        WriteAgentDefinition(fileAccessor, "agents/Foo/agent.mcs.yml");
        WriteText(fileAccessor, "agents/Foo/.agent.json",
            $"{{ \"schemaName\": \"{Bot}.agent.Foo\", \"folderName\": \"Foo\" }}");

        var def = CreateDefinition();

        // Validation passes; GetLocalChanges completes without an exception.
        var (_, _) = synchronizer.GetLocalChanges(def, def, fileAccessor, "token-1");
    }

    [Fact]
    public void GetLocalChanges_RemotePreview_SkipsLinkValidation()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/ws-remote/");
        var fileAccessor = fileAccessorFactory.Create(workspace);
        // Missing link file would fail a local diff, but remote (pull) previews skip the check.
        WriteAgentDefinition(fileAccessor, "agents/Foo/agent.mcs.yml");

        var def = CreateDefinition();

        var (_, _) = synchronizer.GetLocalChanges(def, def, fileAccessor, "token-1", isRemoteChange: true);
    }

    // ---- Schema remap: folder-derived local schema -> real cloud schema via .agent.json ----

    [Fact]
    public void GetLocalChanges_ChildAgent_RemapsSchemaFromLink_NotFlaggedAsNew()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/ws-remap/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        // On disk: a friendly folder whose link points at the real cloud (machine) schema.
        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/TransferFunds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_7_8\", \"folderName\": \"TransferFunds\" }");

        // Cloud carries the server-generated machine schema.
        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        // The LSP-compiled local definition derives the schema from the folder name.
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        // The remap makes local correlate to the existing cloud agent: no Create, no Delete.
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Create && c.SchemaName.Contains(".agent."));
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Delete && c.SchemaName.Contains(".agent."));
    }

    [Fact]
    public void GetLocalChanges_ChildAgent_NoLink_StillFlaggedAsNew()
    {
        // Without the remap a folder-derived schema cannot correlate to the cloud machine
        // schema; this is the broken behavior the .agent.json link fixes. Here we use a remote
        // preview (which skips link validation) to exercise the diff without the missing-link
        // guard, proving the mismatch is what produces the spurious Create.
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath("c:/test/ws-nolink/");
        var fileAccessor = fileAccessorFactory.Create(workspace);

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1", isRemoteChange: true);

        Assert.Contains(changes, c => c.SchemaName == "crd1c_agent.agent.TransferFunds" && c.ChangeType == ChangeType.Create);
    }

    private static BotDefinition CreateDefinition()
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;
        return new BotDefinition().WithEntity(botEntity);
    }

    private static BotDefinition CreateDefinitionWithChildAgent(string agentSchema, string displayName)
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var agent = new DialogComponent(
            schemaName: agentSchema,
            displayName: displayName,
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());
        return new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { agent });
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
