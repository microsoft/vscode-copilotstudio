// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Collections.Immutable;
using System.Text;
using Microsoft.Agents.ObjectModel;
using Microsoft.Agents.Platform.Content;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Moq;
using Xunit;
using static Microsoft.CopilotStudio.Sync.Dataverse.SyncDataverseClient;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ChildAgentLinkFileTests
{
    private const string Bot = "cre98_AgentC1";

    private static IFileAccessor CreateAccessor()
        => new InMemoryFileAccessorFactory().Create(new DirectoryPath("c:/test/workspace/"));

    [Fact]
    public void ListFolders_SchemaFromAnchorMetadata_RoundTrips()
    {
        var accessor = CreateAccessor();
        WriteText(accessor, "agents/Transfer Funds/agent.mcs.yml", $"mcs.metadata:\n  componentName: Transfer Funds\n  schemaName: {Bot}.agent.TransferFunds\nkind: AgentDialog\n");

        var folder = Assert.Single(ChildAgentLinkFile.ListFolders(accessor));
        Assert.Equal("Transfer Funds", folder.FolderName);
        Assert.NotNull(folder.Link);
        Assert.Equal($"{Bot}.agent.TransferFunds", folder.Link!.SchemaName);
        Assert.Equal("Transfer Funds", folder.Link.FolderName);
    }

    [Fact]
    public void ListFolders_LegacyLinkFile_StillResolves()
    {
        var accessor = CreateAccessor();
        WriteAgentDefinition(accessor, "agents/Transfer Funds/agent.mcs.yml");
        WriteText(accessor, "agents/Transfer Funds/.agent.json", $"{{ \"schemaName\": \"{Bot}.agent.TransferFunds\", \"folderName\": \"Transfer Funds\" }}");

        var folder = Assert.Single(ChildAgentLinkFile.ListFolders(accessor));
        Assert.Equal($"{Bot}.agent.TransferFunds", folder.Link!.SchemaName);
    }

    [Fact]
    public void ListFolders_NoChildAgents_Empty()
    {
        var accessor = CreateAccessor();
        WriteText(accessor, "topics/Greeting.mcs.yml", "kind: AdaptiveDialog");

        Assert.Empty(ChildAgentLinkFile.ListFolders(accessor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not valid json")]
    [InlineData("{ }")]
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

    [Fact]
    public void DeleteLink_RemovesSidecar_LeavesAgentDefinition()
    {
        var accessor = CreateAccessor();
        WriteAgentDefinition(accessor, "agents/Transfer Funds/agent.mcs.yml");
        WriteText(accessor, "agents/Transfer Funds/.agent.json", $"{{ \"schemaName\": \"{Bot}.agent.TransferFunds\", \"folderName\": \"Transfer Funds\" }}");
        Assert.True(accessor.Exists(new AgentFilePath("agents/Transfer Funds/.agent.json")));

        ChildAgentLinkFile.DeleteLink(accessor, new AgentFilePath("agents/Transfer Funds/agent.mcs.yml"));

        Assert.False(accessor.Exists(new AgentFilePath("agents/Transfer Funds/.agent.json")));
        Assert.True(accessor.Exists(new AgentFilePath("agents/Transfer Funds/agent.mcs.yml")));
    }

    [Fact]
    public void DeleteLink_NoSidecar_DoesNotThrow()
    {
        var accessor = CreateAccessor();
        WriteAgentDefinition(accessor, "agents/Foo/agent.mcs.yml");

        ChildAgentLinkFile.DeleteLink(accessor, new AgentFilePath("agents/Foo/agent.mcs.yml"));
    }

    [Fact]
    public void DeleteLink_NonChildAgentPath_NoOp()
    {
        var accessor = CreateAccessor();
        WriteText(accessor, "topics/Greeting.mcs.yml", "kind: AdaptiveDialog");

        ChildAgentLinkFile.DeleteLink(accessor, new AgentFilePath("topics/Greeting.mcs.yml"));

        Assert.True(accessor.Exists(new AgentFilePath("topics/Greeting.mcs.yml")));
    }

    [Fact]
    public void GetLocalChanges_ValidLink_RemapsSchema_NotFlaggedAsNew()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-remap/"));

        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/TransferFunds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_7_8\", \"folderName\": \"TransferFunds\" }");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        AssertNoChildAgentCreateOrDelete(changes);
    }

    [Fact]
    public void GetLocalChanges_ValidLink_SchemaMissingFromCloud_OrphanedCloudAgent_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-stale-link/"));
        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/TransferFunds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_7_8\", \"folderName\": \"TransferFunds\" }");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_Other", "Other Agent");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Agent_Other", ex.Message);
        Assert.Contains("has no local folder", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_ValidLink_SchemaMissingFromCloud_NoOrphanedCloudAgent_FlaggedAsCreate()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-link-new-identity/"));
        WriteText(fileAccessor, "agents/Other Agent/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Agent_Other\nkind: AgentDialog\n");
        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/TransferFunds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_7_8\", \"folderName\": \"TransferFunds\" }");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_Other", "Other Agent");
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.Agent_Other", "Other Agent"),
            ("crd1c_agent.agent.TransferFunds", "Transfer Funds"));

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.Equal("crd1c_agent.agent.Agent_7_8", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.Contains(changeSet.BotComponentChanges.OfType<BotComponentInsert>(),
            insert => insert.Component!.SchemaNameString == "crd1c_agent.agent.Agent_7_8");
        Assert.DoesNotContain(changeSet.BotComponentChanges.OfType<BotComponentDelete>(), _ => true);
    }

    [Fact]
    public void GetLocalChanges_NewExplicitSchema_FolderMatchesClaimedAgentDisplayName_FlaggedAsCreate()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-display-collision/"));

        WriteText(fileAccessor, "agents/Agent_7_8/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Agent_7_8\nkind: AgentDialog\n");
        WriteText(fileAccessor, "agents/Transfer Funds/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.BrandNew\nkind: AgentDialog\n");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.Agent_7_8", "Transfer Funds"),
            ("crd1c_agent.agent.TransferFunds", "Brand New"));

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.Equal("crd1c_agent.agent.BrandNew", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.DoesNotContain(changeSet.BotComponentChanges.OfType<BotComponentDelete>(), _ => true);
    }

    [Fact]
    public void GetLocalChanges_LinkToSchemaOfAnotherBot_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-foreign-schema/"));
        WriteText(fileAccessor, "agents/Imported/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: otherbot.agent.Foreign\nkind: AgentDialog\n");

        var cloud = CreateDefinitionWithChildAgents();
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.Imported", "Imported");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("otherbot.agent.Foreign", ex.Message);
        Assert.Contains("is not a valid child agent schema", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_LinkToSchemaOfExistingNonAgentComponent_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-alias-topic/"));
        WriteText(fileAccessor, "agents/NewAgent/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Existing\nkind: AgentDialog\n");

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var existingTopic = new DialogComponent(
            schemaName: "crd1c_agent.agent.Existing",
            displayName: "Existing",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AdaptiveDialog());
        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { existingTopic });
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.NewAgent", "New Agent");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Existing", ex.Message);
        Assert.Contains("already belongs to a different cloud component", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_TwoFoldersLinkedToSameCloudAgent_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-duplicate-link/"));
        WriteText(fileAccessor, "agents/First/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Agent_7_8\nkind: AgentDialog\n");
        WriteText(fileAccessor, "agents/Second/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Agent_7_8\nkind: AgentDialog\n");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Agent 7 8");
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.First", "First"),
            ("crd1c_agent.agent.Second", "Second"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Agent_7_8", ex.Message);
        Assert.Contains("already belongs to a different cloud component", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_NewFolderNameMatchesExistingAgentExplicitSchema_DoesNotRemapExistingAgent()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-derived-collision/"));

        WriteText(fileAccessor, "agents/Original/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Existing\nkind: AgentDialog\n");
        WriteText(fileAccessor, "agents/Existing/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.New\nkind: AgentDialog\n");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Existing", "Original");
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.Existing", "Original"),
            ("crd1c_agent.agent.New", "New"));

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.Equal("crd1c_agent.agent.New", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.DoesNotContain(changeSet.BotComponentChanges.OfType<BotComponentDelete>(), _ => true);
        Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentInsert>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetLocalChanges_NewLinkedFolderBesideUnlinkedLegacyFolder_ResolvesRegardlessOfEnumerationOrder(bool newFolderFirst)
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath($"c:/test/ws-order-{newFolderFirst}/"));

        var newFolder = () => WriteText(fileAccessor, "agents/Friendly/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.NewFriendly\nkind: AgentDialog\n");
        var legacyFolder = () => WriteAgentDefinition(fileAccessor, "agents/Machine/agent.mcs.yml");

        if (newFolderFirst)
        {
            newFolder();
            legacyFolder();
        }
        else
        {
            legacyFolder();
            newFolder();
        }

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Machine", "Friendly");
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.NewFriendly", "NewFriendly"),
            ("crd1c_agent.agent.Machine", "Friendly"));

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.Equal("crd1c_agent.agent.NewFriendly", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.DoesNotContain(changeSet.BotComponentChanges.OfType<BotComponentDelete>(), _ => true);
    }

    [Fact]
    public void GetLocalChanges_TwoNewFoldersResolveToSameSchema_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-new-duplicate/"));
        WriteText(fileAccessor, "agents/First/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Shared\nkind: AgentDialog\n");
        WriteText(fileAccessor, "agents/Second/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Shared\nkind: AgentDialog\n");

        var cloud = CreateDefinitionWithChildAgents();
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.First", "First"),
            ("crd1c_agent.agent.Second", "Second"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Shared", ex.Message);
        Assert.Contains("already belongs to a different cloud component", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_TwoNewFoldersProjectToSameDerivedSchema_KeepsExplicitSchemas()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-derived-duplicate/"));
        WriteText(fileAccessor, "agents/New Agent/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.First\nkind: AgentDialog\n");
        WriteText(fileAccessor, "agents/NewAgent/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Second\nkind: AgentDialog\n");

        var cloud = CreateDefinitionWithChildAgents();
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.First", "New Agent"),
            ("crd1c_agent.agent.Second", "NewAgent"));

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        var created = changes.Where(c => c.ChangeType == ChangeType.Create && c.SchemaName.Contains(".agent.")).Select(c => c.SchemaName).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "crd1c_agent.agent.First", "crd1c_agent.agent.Second" }, created);
        Assert.DoesNotContain(changeSet.BotComponentChanges.OfType<BotComponentDelete>(), _ => true);
    }

    [Fact]
    public void GetLocalChanges_UnchangedCloudAgentsWithCollidingFolderNames_ReportsNoChildAgentChanges()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-colliding-folders/"));
        WriteText(fileAccessor, "agents/New Agent/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.First\nkind: AgentDialog\n");
        WriteText(fileAccessor, "agents/NewAgent/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Second\nkind: AgentDialog\n");

        var cloud = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.First", "New Agent"),
            ("crd1c_agent.agent.Second", "NewAgent"));
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.First", "New Agent"),
            ("crd1c_agent.agent.Second", "NewAgent"));

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        AssertNoChildAgentCreateOrDelete(changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetLocalChanges_UnlinkedFoldersCrossMatchingDisplayNames_ResolveRegardlessOfEnumerationOrder(bool reverseOrder)
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var inner = fileAccessorFactory.Create(new DirectoryPath($"c:/test/ws-cross-match-{reverseOrder}/"));
        WriteAgentDefinition(inner, "agents/Xyz/agent.mcs.yml");
        WriteAgentDefinition(inner, "agents/Friendly/agent.mcs.yml");
        var fileAccessor = new OrderedFileAccessor(inner, reverseOrder ? new[] { "agents/Friendly/", "agents/Xyz/" } : new[] { "agents/Xyz/", "agents/Friendly/" });

        var cloud = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.Friendly", "Xyz"),
            ("crd1c_agent.agent.Machine", "Friendly"));
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.Friendly", "Xyz"),
            ("crd1c_agent.agent.Machine", "Friendly"));

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        AssertNoChildAgentCreateOrDelete(changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetLocalChanges_UnlinkedFolderTakingClaimedDerivedSchema_ThrowsRegardlessOfEnumerationOrder(bool reverseOrder)
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var inner = fileAccessorFactory.Create(new DirectoryPath($"c:/test/ws-claimed-derived-{reverseOrder}/"));
        WriteAgentDefinition(inner, "agents/Machine/agent.mcs.yml");
        WriteAgentDefinition(inner, "agents/Friendly/agent.mcs.yml");
        var fileAccessor = new OrderedFileAccessor(inner, reverseOrder ? new[] { "agents/Friendly/", "agents/Machine/" } : new[] { "agents/Machine/", "agents/Friendly/" });

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Machine", "Friendly");
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.Machine", "Friendly"),
            ("crd1c_agent.agent.Friendly", "Friendly"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Machine", ex.Message);
        Assert.Contains("Use a different schema name or folder name", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_TwoUnlinkedFoldersMatchingSameCloudDisplayName_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-same-display/"));
        WriteAgentDefinition(fileAccessor, "agents/New Agent/agent.mcs.yml");
        WriteAgentDefinition(fileAccessor, "agents/NewAgent/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Only", "New Agent");
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.Only", "New Agent"),
            ("crd1c_agent.agent.NewAgent", "NewAgent"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Only", ex.Message);
        Assert.Contains("Rename one of the folders", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_LinkWithEmptyShortSchemaName_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-empty-schema/"));
        WriteText(fileAccessor, "agents/New/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: 'crd1c_agent.agent.'\nkind: AgentDialog\n");

        var cloud = CreateDefinitionWithChildAgents();
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.New", "New");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("is not a valid child agent schema", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_ValidLink_EmptyCloudCache_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-link-empty-cache/"));
        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/TransferFunds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_7_8\", \"folderName\": \"TransferFunds\" }");

        var cloud = CreateDefinitionWithChildAgents();
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var exception = Record.Exception(() => synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));

        Assert.Null(exception);
    }

    [Fact]
    public void GetLocalChanges_StaleLink_DoesNotSilentlySelfHealByDisplayName_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-stale-link-heal/"));
        WriteAgentDefinition(fileAccessor, "agents/Transfer Funds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/Transfer Funds/.agent.json",
            "{ \"schemaName\": \"crd1c_agent.agent.Agent_STALE\", \"folderName\": \"Transfer Funds\" }");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.TransferFunds", "Transfer Funds");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("crd1c_agent.agent.Agent_STALE", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_MissingLink_SelfHealsByDisplayName_NotFlaggedAsNew()
    {
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
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-heal-schema/"));
        WriteAgentDefinition(fileAccessor, "agents/Agent_7_8/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        AssertNoChildAgentCreateOrDelete(changes);
    }

    [Fact]
    public void GetLocalChanges_MissingLink_SchemaNameFolder_EmptyCloudCache_DoesNotThrow()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-reattach-nolink/"));
        WriteAgentDefinition(fileAccessor, "agents/Agent/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgents();
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent", "Agent Child 1");

        var exception = Record.Exception(() => synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));

        Assert.Null(exception);
    }

    [Fact]
    public void GetLocalChanges_MissingLink_RenamedFolder_OrphanedCloudAgent_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-orphan/"));
        WriteAgentDefinition(fileAccessor, "agents/Ghost/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.Ghost", "Ghost");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("agents/Ghost", ex.Message);
        Assert.Contains("crd1c_agent.agent.Agent_7_8", ex.Message);
        Assert.Contains("has no local folder", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_MissingLink_NoCloudChildAgents_FlaggedAsCreate()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-new-only/"));
        WriteAgentDefinition(fileAccessor, "agents/Ghost/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgents();
        var local = CreateDefinitionWithChildAgent("crd1c_agent.agent.Ghost", "Ghost");

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.Equal("crd1c_agent.agent.Ghost", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.Contains(changeSet.BotComponentChanges.OfType<BotComponentInsert>(),
            insert => insert.Component!.SchemaNameString == "crd1c_agent.agent.Ghost");
    }

    [Fact]
    public void GetLocalChanges_NewLocalChildAgent_AlongsideClonedChildAgent_CreatesOnlyTheNewOne()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-new-child/"));

        WriteText(fileAccessor, "agents/Transfer Funds/agent.mcs.yml",
            "mcs.metadata:\n  componentName: Transfer Funds\n  schemaName: crd1c_agent.agent.Agent_7_8\nkind: AgentDialog\n");
        WriteAgentDefinition(fileAccessor, "agents/Refunds/agent.mcs.yml");

        var cloud = CreateDefinitionWithChildAgent("crd1c_agent.agent.Agent_7_8", "Transfer Funds");
        var local = CreateDefinitionWithChildAgents(
            ("crd1c_agent.agent.TransferFunds", "Transfer Funds"),
            ("crd1c_agent.agent.Refunds", "Refunds"));

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        Assert.Equal("crd1c_agent.agent.Refunds", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Delete && c.SchemaName.Contains(".agent."));
        Assert.Contains(changeSet.BotComponentChanges.OfType<BotComponentInsert>(),
            insert => insert.Component!.SchemaNameString == "crd1c_agent.agent.Refunds");
        Assert.False(fileAccessor.Exists(new AgentFilePath(".mcs/botdefinition.json")));
    }

    [Fact]
    public void GetLocalChanges_NewLocalChildAgent_WithOwnComponent_DefersChildUntilParentExists()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-new-child-nested/"));
        WriteAgentDefinition(fileAccessor, "agents/Refunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/Refunds/topics/StartRefund.mcs.yml", "kind: AdaptiveDialog");

        var cloud = CreateDefinitionWithChildAgents();
        var newAgentId = Guid.NewGuid();
        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var local = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            new DialogComponent(
                schemaName: "crd1c_agent.agent.Refunds",
                displayName: "Refunds",
                description: string.Empty,
                id: newAgentId,
                parentBotComponentId: default,
                dialog: new AgentDialog()),
            new DialogComponent(
                schemaName: "crd1c_agent.topic.StartRefund",
                displayName: "StartRefund",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: new BotComponentId(newAgentId),
                dialog: new AdaptiveDialog()),
        });

        var (_, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1", isRemoteChange: false, deferMissingParents: true, out var deferredMissingParent);

        Assert.Equal("crd1c_agent.agent.Refunds", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.True(deferredMissingParent);
        Assert.DoesNotContain(changes, c => c.SchemaName == "crd1c_agent.topic.StartRefund");
    }

    [Fact]
    public void GetLocalChanges_MissingLink_AmbiguousMatch_Throws()
    {
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
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-remote/"));
        WriteAgentDefinition(fileAccessor, "agents/Foo/agent.mcs.yml");

        var def = CreateDefinition();

        var (_, _) = synchronizer.GetLocalChanges(def, def, fileAccessor, "token-1", isRemoteChange: true);
    }

    [Fact]
    public async Task PushLocalChangesAsync_NewLocalChildAgentFolder_CreatesChildAgentInCloud()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-new-push-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var cloudAgent = new DialogComponent(
            schemaName: $"{Bot}.agent.Agent_2qD",
            displayName: "BalanceAgent",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();

        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        Assert.True(fileAccessor.Exists(new AgentFilePath("agents/BalanceAgent/agent.mcs.yml")));

        WriteAgentDefinition(fileAccessor, "agents/Refunds/agent.mcs.yml");

        var pushedInserts = new List<string>();
        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var confirmed = incoming.BotComponentChanges.Select(change =>
                {
                    if (change is not BotComponentInsert insert || insert.Component is not BotComponentBase component)
                    {
                        return change;
                    }

                    pushedInserts.Add(component.SchemaNameString!);
                    var builder = component.ToBuilder();
                    builder.Id = Guid.NewGuid();
                    return new BotComponentInsert(builder.Build());
                });
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var workspaceDefinition = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            new DialogComponent(
                schemaName: $"{Bot}.agent.BalanceAgent",
                displayName: "BalanceAgent",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: default,
                dialog: new AgentDialog()),
            new DialogComponent(
                schemaName: $"{Bot}.agent.Refunds",
                displayName: "Refunds",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: default,
                dialog: new AgentDialog()),
        });

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        Assert.Equal(new[] { $"{Bot}.agent.Refunds" }, pushedInserts);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        var cachedAgents = finalCache.Components.OfType<DialogComponent>().Where(c => c.RootElement is AgentDialog).Select(c => c.SchemaNameString).ToList();
        Assert.Contains($"{Bot}.agent.Refunds", cachedAgents);
        Assert.Contains($"{Bot}.agent.Agent_2qD", cachedAgents);
    }

    [Fact]
    public void GetLocalChanges_DeletedChildAgentFolder_OrdersChildComponentDeletesBeforeTheAgent()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-delete-child/"));

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var childAgentId = Guid.NewGuid();
        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            new DialogComponent(
                schemaName: "crd1c_agent.agent.Agentchild2",
                displayName: "Agent child 2",
                description: string.Empty,
                id: childAgentId,
                parentBotComponentId: default,
                dialog: new AgentDialog()),
            CreateKnowledgeFileComponent("crd1c_agent.file.abchoa5", "abc hoa 5.txt", new BotComponentId(childAgentId)),
            new DialogComponent(
                schemaName: "crd1c_agent.action.MicrosoftTeams-Getasection",
                displayName: "Microsoft Teams - Get a section",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: new BotComponentId(childAgentId),
                dialog: new AdaptiveDialog()),
        });

        var local = new BotDefinition().WithEntity(botEntity);

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        var deletedIds = changeSet.BotComponentChanges.OfType<BotComponentDelete>().Select(delete => delete.BotComponentId.Value).ToList();
        Assert.Equal(3, deletedIds.Count);
        Assert.Equal(childAgentId, deletedIds[^1]);
        Assert.Equal(3, changes.Count(c => c.ChangeType == ChangeType.Delete));
    }

    [Fact]
    public void GetLocalChanges_DeletedNestedComponents_OrdersDeepestDescendantsFirst()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-delete-nested/"));

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var agentId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            new DialogComponent(
                schemaName: "crd1c_agent.agent.Nested",
                displayName: "Nested",
                description: string.Empty,
                id: agentId,
                parentBotComponentId: default,
                dialog: new AgentDialog()),
            new DialogComponent(
                schemaName: "crd1c_agent.action.NestedAction",
                displayName: "NestedAction",
                description: string.Empty,
                id: actionId,
                parentBotComponentId: new BotComponentId(agentId),
                dialog: new AdaptiveDialog()),
            CreateKnowledgeFileComponent("crd1c_agent.file.NestedFile", "nested.txt", new BotComponentId(actionId), fileId),
        });

        var local = new BotDefinition().WithEntity(botEntity);

        var (changeSet, _) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        var deletedIds = changeSet.BotComponentChanges.OfType<BotComponentDelete>().Select(delete => delete.BotComponentId.Value).ToList();
        Assert.Equal(new[] { fileId, actionId, agentId }, deletedIds);
    }

    [Fact]
    public void GetLocalChanges_DeletedDeepHierarchy_OrdersEveryDescendantBeforeItsParent()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-delete-deep/"));

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var chain = new List<BotComponentBase>();
        for (var level = 0; level < 40; level++)
        {
            chain.Add(new DialogComponent(
                schemaName: $"crd1c_agent.topic.Level{level}",
                displayName: $"Level{level}",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: level == 0 ? default : chain[level - 1].Id,
                dialog: new AdaptiveDialog()));
        }

        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(chain.ToArray());
        var local = new BotDefinition().WithEntity(botEntity);

        var (changeSet, _) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        var deletedIds = changeSet.BotComponentChanges.OfType<BotComponentDelete>().Select(delete => delete.BotComponentId.Value).ToList();
        Assert.Equal(chain.Select(c => c.Id.Value).Reverse(), deletedIds);
    }

    [Fact]
    public void GetLocalChanges_DeletedComponentsWithParentCycle_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-delete-cycle/"));

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            new DialogComponent(
                schemaName: "crd1c_agent.topic.First",
                displayName: "First",
                description: string.Empty,
                id: firstId,
                parentBotComponentId: new BotComponentId(secondId),
                dialog: new AdaptiveDialog()),
            new DialogComponent(
                schemaName: "crd1c_agent.topic.Second",
                displayName: "Second",
                description: string.Empty,
                id: secondId,
                parentBotComponentId: new BotComponentId(firstId),
                dialog: new AdaptiveDialog()),
        });

        var local = new BotDefinition().WithEntity(botEntity);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("circular parent relationship", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_DeletedDescendantOfParentCycle_Throws()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-delete-cycle-leaf/"));

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            new DialogComponent(
                schemaName: "crd1c_agent.topic.First",
                displayName: "First",
                description: string.Empty,
                id: firstId,
                parentBotComponentId: new BotComponentId(secondId),
                dialog: new AdaptiveDialog()),
            new DialogComponent(
                schemaName: "crd1c_agent.topic.Second",
                displayName: "Second",
                description: string.Empty,
                id: secondId,
                parentBotComponentId: new BotComponentId(firstId),
                dialog: new AdaptiveDialog()),
            CreateKnowledgeFileComponent("crd1c_agent.file.Leaf", "leaf.txt", new BotComponentId(firstId)),
        });

        var local = new BotDefinition().WithEntity(botEntity);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1"));
        Assert.Contains("circular parent relationship", ex.Message);
    }

    [Fact]
    public void GetLocalChanges_DeletedSiblingsWithoutNesting_PreservesCloudCacheOrder()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-delete-flat/"));

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var topics = Enumerable.Range(0, 4).Select(index => (BotComponentBase)new DialogComponent(
            schemaName: $"crd1c_agent.topic.Flat{index}",
            displayName: $"Flat{index}",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AdaptiveDialog())).ToArray();

        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(topics);
        var local = new BotDefinition().WithEntity(botEntity);

        var (changeSet, _) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        var deletedIds = changeSet.BotComponentChanges.OfType<BotComponentDelete>().Select(delete => delete.BotComponentId.Value).ToList();
        Assert.Equal(topics.Select(c => c.Id.Value), deletedIds);
    }

    [Fact]
    public void GetLocalChanges_DeletedChildAgent_AlongsideRetainedChildAgent_OnlyDeletesTheRemovedTree()
    {
        var (synchronizer, fileAccessorFactory, _) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var fileAccessor = fileAccessorFactory.Create(new DirectoryPath("c:/test/ws-delete-one-of-two/"));
        WriteText(fileAccessor, "agents/Agent Child 1/agent.mcs.yml",
            "mcs.metadata:\n  schemaName: crd1c_agent.agent.Agentchild1\nkind: AgentDialog\n");

        var botEntity = CodeSerializer.Deserialize<BotEntity>("kind: Bot\nschemaName: crd1c_agent")!;
        var retainedAgent = new DialogComponent(
            schemaName: "crd1c_agent.agent.Agentchild1",
            displayName: "Agent Child 1",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());
        var removedAgentId = Guid.NewGuid();
        var removedAgent = new DialogComponent(
            schemaName: "crd1c_agent.agent.Agentchild2",
            displayName: "Agent child 2",
            description: string.Empty,
            id: removedAgentId,
            parentBotComponentId: default,
            dialog: new AgentDialog());
        var removedFile = CreateKnowledgeFileComponent("crd1c_agent.file.abchoa5", "abc hoa 5.txt", new BotComponentId(removedAgentId));

        var cloud = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { retainedAgent, removedAgent, removedFile });
        var local = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[] { retainedAgent });

        var (changeSet, changes) = synchronizer.GetLocalChanges(local, cloud, fileAccessor, "token-1");

        var deletedIds = changeSet.BotComponentChanges.OfType<BotComponentDelete>().Select(delete => delete.BotComponentId.Value).ToList();
        Assert.Equal(new[] { removedFile.Id.Value, removedAgentId }, deletedIds);
        Assert.DoesNotContain(changes, c => c.SchemaName == "crd1c_agent.agent.Agentchild1");
    }

    [Fact]
    public async Task PushLocalChangesAsync_DeletedChildAgentFolder_SendsChildDeletesBeforeAgentAndClearsCache()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-delete-push-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = new DialogComponent(
            schemaName: $"{Bot}.agent.Agent_2qD",
            displayName: "BalanceAgent",
            description: string.Empty,
            id: childAgentId,
            parentBotComponentId: default,
            dialog: new AgentDialog());
        var cloudAction = new DialogComponent(
            schemaName: $"{Bot}.action.TeamsGetasection",
            displayName: "TeamsGetasection",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: new BotComponentId(childAgentId),
            dialog: new AdaptiveDialog());
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(cloudAgent), new BotComponentInsert(cloudAction) }, botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();

        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        fileAccessor.DeleteDirectory(new AgentFilePath("agents/BalanceAgent"));

        var deletedIdsAtService = new List<Guid>();
        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                deletedIdsAtService.AddRange(incoming.BotComponentChanges.OfType<BotComponentDelete>().Select(delete => delete.BotComponentId.Value));
                return Task.FromResult(new PvaComponentChangeSet(incoming.BotComponentChanges, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        Assert.Equal(new[] { cloudAction.Id.Value, childAgentId }, deletedIdsAtService);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Empty(finalCache.Components.OfType<DialogComponent>().Where(c => c.RootElement is AgentDialog));
        Assert.DoesNotContain(finalCache.Components, c => c.SchemaNameString == $"{Bot}.action.TeamsGetasection");

        var (_, remainingChanges) = await synchronizer.GetLocalChangesAsync(workspace, workspaceDefinition, CancellationToken.None);
        Assert.Empty(remainingChanges);
    }

    [Fact]
    public async Task PushLocalChangesAsync_NewChildAgentWithOwnComponent_SendsChildWithServerAssignedParentId()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-two-pass-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();

        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        WriteAgentDefinition(fileAccessor, "agents/Refunds/agent.mcs.yml");

        var serverAgentId = Guid.NewGuid();
        var passes = new List<List<(string Schema, BotComponentId Parent)>>();
        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                var pass = new List<(string, BotComponentId)>();
                var confirmed = incoming.BotComponentChanges.Select(change =>
                {
                    if (change is not BotComponentInsert insert || insert.Component is not BotComponentBase component)
                    {
                        return change;
                    }

                    pass.Add((component.SchemaNameString!, component.ParentBotComponentId));
                    var builder = component.ToBuilder();
                    builder.Id = component.RootElement is AgentDialog ? serverAgentId : Guid.NewGuid();
                    return new BotComponentInsert(builder.Build());
                }).ToList();
                passes.Add(pass);
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var localAgentId = Guid.NewGuid();
        var workspaceDefinition = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            new DialogComponent(
                schemaName: $"{Bot}.agent.Refunds",
                displayName: "Refunds",
                description: string.Empty,
                id: localAgentId,
                parentBotComponentId: default,
                dialog: new AgentDialog()),
            new DialogComponent(
                schemaName: $"{Bot}.topic.StartRefund",
                displayName: "StartRefund",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: new BotComponentId(localAgentId),
                dialog: new AdaptiveDialog()),
        });

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        Assert.Equal(2, passes.Count);
        Assert.Equal($"{Bot}.agent.Refunds", Assert.Single(passes[0]).Schema);
        var childInsert = Assert.Single(passes[1]);
        Assert.Equal($"{Bot}.topic.StartRefund", childInsert.Schema);
        Assert.Equal(new BotComponentId(serverAgentId), childInsert.Parent);
    }

    [Fact]
    public async Task GetLocalChangesAsync_PreviewOfNewChildAgent_LeavesCloudCacheAndChangeTokenUntouched()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-preview-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var cloudAgent = new DialogComponent(
            schemaName: $"{Bot}.agent.Agent_2qD",
            displayName: "BalanceAgent",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();

        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        WriteAgentDefinition(fileAccessor, "agents/Refunds/agent.mcs.yml");

        var cacheBefore = ReadFileText(fileAccessor, ".mcs/botdefinition.json");
        var tokenBefore = ReadFileText(fileAccessor, ".mcs/changetoken.txt");

        var workspaceDefinition = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            cloudAgent,
            new DialogComponent(
                schemaName: $"{Bot}.agent.Refunds",
                displayName: "Refunds",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: default,
                dialog: new AgentDialog()),
        });

        var (_, changes) = await synchronizer.GetLocalChangesAsync(workspace, workspaceDefinition, CancellationToken.None);

        Assert.Contains(changes, c => c.ChangeType == ChangeType.Create && c.SchemaName == $"{Bot}.agent.Refunds");
        Assert.Equal(cacheBefore, ReadFileText(fileAccessor, ".mcs/botdefinition.json"));
        Assert.Equal(tokenBefore, ReadFileText(fileAccessor, ".mcs/changetoken.txt"));
        mockIsland.Verify(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PushLocalChangesAsync_WhenServiceFails_LeavesCloudCacheAndChangeTokenUntouched()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-failed-push-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var cloudAgent = new DialogComponent(
            schemaName: $"{Bot}.agent.Agent_2qD",
            displayName: "BalanceAgent",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        WriteAgentDefinition(fileAccessor, "agents/Refunds/agent.mcs.yml");

        var cacheBefore = ReadFileText(fileAccessor, ".mcs/botdefinition.json");
        var tokenBefore = ReadFileText(fileAccessor, ".mcs/changetoken.txt");

        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("service unavailable"));

        var workspaceDefinition = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            cloudAgent,
            new DialogComponent(
                schemaName: $"{Bot}.agent.Refunds",
                displayName: "Refunds",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: default,
                dialog: new AgentDialog()),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None));

        Assert.Equal(cacheBefore, ReadFileText(fileAccessor, ".mcs/botdefinition.json"));
        Assert.Equal(tokenBefore, ReadFileText(fileAccessor, ".mcs/changetoken.txt"));
    }

    [Fact]
    public async Task PushLocalChangesAsync_WhenSecondPassFails_LeavesFirstPassConfirmationOnlyInCache()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-second-pass-fail-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        WriteAgentDefinition(fileAccessor, "agents/Refunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/Refunds/topics/StartRefund.mcs.yml", "kind: AdaptiveDialog");

        var serverAgentId = Guid.NewGuid();
        var call = 0;
        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                if (call++ > 0)
                {
                    throw new InvalidOperationException("second pass failed");
                }

                var confirmed = incoming.BotComponentChanges.Select(change =>
                {
                    if (change is not BotComponentInsert insert || insert.Component is not BotComponentBase component)
                    {
                        return change;
                    }

                    var builder = component.ToBuilder();
                    builder.Id = serverAgentId;
                    return new BotComponentInsert(builder.Build());
                }).ToList();
                return Task.FromResult(new PvaComponentChangeSet(confirmed, incoming.Bot, "token-2"));
            });

        var localAgentId = Guid.NewGuid();
        var workspaceDefinition = new BotDefinition().WithEntity(botEntity).WithComponents(new BotComponentBase[]
        {
            new DialogComponent(
                schemaName: $"{Bot}.agent.Refunds",
                displayName: "Refunds",
                description: string.Empty,
                id: localAgentId,
                parentBotComponentId: default,
                dialog: new AgentDialog()),
            new DialogComponent(
                schemaName: $"{Bot}.topic.StartRefund",
                displayName: "StartRefund",
                description: string.Empty,
                id: Guid.NewGuid(),
                parentBotComponentId: new BotComponentId(localAgentId),
                dialog: new AdaptiveDialog()),
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None));

        Assert.Equal("second pass failed", exception.Message);
        Assert.Equal(2, call);

        var cache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Equal(serverAgentId, Assert.Single(cache.Components.OfType<DialogComponent>().Where(c => c.RootElement is AgentDialog)).Id.Value);
        Assert.DoesNotContain(cache.Components, c => c.SchemaNameString == $"{Bot}.topic.StartRefund");
        Assert.Equal("token-2", ReadFileText(fileAccessor, ".mcs/changetoken.txt"));
    }

    [Fact]
    public async Task PushLocalChangesAsync_DeletedChildAgentWithKnowledgeFileAndAction_SendsAgentLast()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-delete-full-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var childAgentId = Guid.NewGuid();
        var cloudAgent = new DialogComponent(
            schemaName: $"{Bot}.agent.Agentchild2",
            displayName: "Agent child 2",
            description: string.Empty,
            id: childAgentId,
            parentBotComponentId: default,
            dialog: new AgentDialog());
        var cloudAction = new DialogComponent(
            schemaName: $"{Bot}.action.MicrosoftTeams-Getasection",
            displayName: "MicrosoftTeams-Getasection",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: new BotComponentId(childAgentId),
            dialog: new AdaptiveDialog());
        var cloudKnowledgeFile = CreateKnowledgeFileComponent($"{Bot}.file.abchoa5", "abc hoa 5.txt", new BotComponentId(childAgentId));

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(
                new BotComponentChange[] { new BotComponentInsert(cloudAgent), new BotComponentInsert(cloudKnowledgeFile), new BotComponentInsert(cloudAction) },
                botEntity,
                "token-1"));

        var mockDataverse = CreateMockDataverseClient();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        fileAccessor.DeleteDirectory(new AgentFilePath("agents/Agent child 2"));

        var deletedIdsAtService = new List<Guid>();
        mockIsland
            .Setup(x => x.SaveChangesAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<PvaComponentChangeSet>(), It.IsAny<CancellationToken>()))
            .Returns<AuthoringOperationContextBase, PvaComponentChangeSet, CancellationToken>((_, incoming, _) =>
            {
                deletedIdsAtService.AddRange(incoming.BotComponentChanges.OfType<BotComponentDelete>().Select(delete => delete.BotComponentId.Value));
                return Task.FromResult(new PvaComponentChangeSet(incoming.BotComponentChanges, incoming.Bot, Guid.NewGuid().ToString("N")));
            });

        var workspaceDefinition = new BotDefinition().WithEntity(botEntity);

        await synchronizer.PushLocalChangesAsync(workspace, operationContext, workspaceDefinition, mockDataverse.Object, syncInfo, cloudFlowMetadata: null, ImmutableArray<AIPromptMetadata>.Empty, CancellationToken.None);

        Assert.Equal(3, deletedIdsAtService.Count);
        Assert.Equal(childAgentId, deletedIdsAtService[^1]);
        Assert.Contains(cloudAction.Id.Value, deletedIdsAtService);
        Assert.Contains(cloudKnowledgeFile.Id.Value, deletedIdsAtService);

        var finalCache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Empty(finalCache.Components.OfType<DialogComponent>().Where(c => c.RootElement is AgentDialog));
    }

    [Fact]
    public async Task ReadWorkspaceDefinitionAsync_NewChildAgentFolderNamedAfterExistingSchema_KeepsBothIdentities()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-reader-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var cloudAgent = new DialogComponent(
            schemaName: $"{Bot}.agent.Existing",
            displayName: "Original",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        Assert.True(fileAccessor.Exists(new AgentFilePath("agents/Original/agent.mcs.yml")));

        WriteText(fileAccessor, "agents/Existing/agent.mcs.yml",
            $"mcs.metadata:\n  componentName: Existing\n  schemaName: {Bot}.agent.New\nkind: AgentDialog\n");

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var readSchemas = definition.Components.OfType<DialogComponent>()
            .Where(c => c.RootElement is AgentDialog)
            .Select(c => c.SchemaNameString)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { $"{Bot}.agent.Existing", $"{Bot}.agent.New" }, readSchemas);

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, definition, CancellationToken.None);

        Assert.Equal($"{Bot}.agent.New", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.DoesNotContain(changeSet.BotComponentChanges.OfType<BotComponentDelete>(), _ => true);
        Assert.Single(changeSet.BotComponentChanges.OfType<BotComponentInsert>());

        var cache = WorkspaceSynchronizer.ReadCloudCacheSnapshot(fileAccessor)!;
        Assert.Equal(new[] { $"{Bot}.agent.Existing" }, cache.Components.OfType<DialogComponent>().Where(c => c.RootElement is AgentDialog).Select(c => c.SchemaNameString).ToList());
    }

    [Fact]
    public async Task ReadWorkspaceDefinitionAsync_NewChildAgentFolderWithLegacyLink_UsesLinkedSchema()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-reader-legacy-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(Array.Empty<BotComponentChange>(), botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        WriteAgentDefinition(fileAccessor, "agents/TransferFunds/agent.mcs.yml");
        WriteText(fileAccessor, "agents/TransferFunds/.agent.json",
            $"{{ \"schemaName\": \"{Bot}.agent.Agent_7_8\", \"folderName\": \"TransferFunds\" }}");

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var agent = Assert.Single(definition.Components.OfType<DialogComponent>().Where(c => c.RootElement is AgentDialog));
        Assert.Equal($"{Bot}.agent.Agent_7_8", agent.SchemaNameString);
    }

    [Fact]
    public async Task ReadWorkspaceDefinitionAsync_NewFolderMatchingExistingAgentDisplayName_KeepsEachFolderContent()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-display-collision-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var cloudAgent = new DialogComponent(
            schemaName: $"{Bot}.agent.Machine",
            displayName: "New Agent",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        Assert.True(fileAccessor.Exists(new AgentFilePath("agents/New Agent/agent.mcs.yml")));

        WriteText(fileAccessor, "agents/NewAgent/agent.mcs.yml",
            $"mcs.metadata:\n  componentName: Brand New\n  schemaName: {Bot}.agent.New\nkind: AgentDialog\n");

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var agentsBySchema = definition.Components.OfType<DialogComponent>()
            .Where(c => c.RootElement is AgentDialog)
            .ToDictionary(c => c.SchemaNameString!, c => c.DisplayName);
        Assert.Equal("New Agent", agentsBySchema[$"{Bot}.agent.Machine"]);
        Assert.Equal("Brand New", agentsBySchema[$"{Bot}.agent.New"]);

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, definition, CancellationToken.None);

        Assert.Equal($"{Bot}.agent.New", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.DoesNotContain(changeSet.BotComponentChanges.OfType<BotComponentDelete>(), _ => true);
    }

    [Fact]
    public async Task ReadWorkspaceDefinitionAsync_NewFolderOnExistingAgentProjectedPath_KeepsBothIdentities()
    {
        var (synchronizer, fileAccessorFactory, mockIsland) = ComponentWriterDefensiveTests.CreateSyncInfrastructure();
        var workspace = new DirectoryPath($"c:/test/child-agent-projected-path-{Guid.NewGuid():N}/");
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;

        var cloudAgent = new DialogComponent(
            schemaName: $"{Bot}.agent.Machine",
            displayName: "Friendly",
            description: string.Empty,
            id: Guid.NewGuid(),
            parentBotComponentId: default,
            dialog: new AgentDialog());
        mockIsland
            .Setup(x => x.GetComponentsAsync(It.IsAny<AuthoringOperationContextBase>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PvaComponentChangeSet(new BotComponentChange[] { new BotComponentInsert(cloudAgent) }, botEntity, "token-1"));

        var mockDataverse = CreateMockDataverseClient();
        var operationContext = ComponentWriterDefensiveTests.CreateMockOperationContext();
        var syncInfo = new AgentSyncInfo { AgentId = Guid.NewGuid() };
        await synchronizer.CloneChangesAsync(workspace, new ReferenceTracker(), operationContext, mockDataverse.Object, syncInfo, CancellationToken.None);

        var fileAccessor = (InMemoryFileAccessor)fileAccessorFactory.Create(workspace);
        fileAccessor.DeleteDirectory(new AgentFilePath("agents/Friendly"));
        WriteText(fileAccessor, "agents/Machine/agent.mcs.yml",
            $"mcs.metadata:\n  componentName: Friendly\n  schemaName: {Bot}.agent.Machine\nkind: AgentDialog\n");
        WriteText(fileAccessor, "agents/Friendly/agent.mcs.yml",
            $"mcs.metadata:\n  componentName: Brand New\n  schemaName: {Bot}.agent.New\nkind: AgentDialog\n");

        var definition = await synchronizer.ReadWorkspaceDefinitionAsync(workspace, CancellationToken.None);

        var agentSchemas = definition.Components.OfType<DialogComponent>()
            .Where(c => c.RootElement is AgentDialog)
            .Select(c => c.SchemaNameString)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { $"{Bot}.agent.Machine", $"{Bot}.agent.New" }, agentSchemas);

        var (changeSet, changes) = await synchronizer.GetLocalChangesAsync(workspace, definition, CancellationToken.None);

        Assert.Equal($"{Bot}.agent.New", SingleChildAgentChange(changes, ChangeType.Create).SchemaName);
        Assert.DoesNotContain(changeSet.BotComponentChanges.OfType<BotComponentDelete>(), _ => true);
    }

    private static Mock<ISyncDataverseClient> CreateMockDataverseClient()
    {
        var mockDataverse = new Mock<ISyncDataverseClient>();
        mockDataverse
            .Setup(x => x.DownloadAllWorkflowsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<WorkflowMetadata>());
        mockDataverse
            .Setup(x => x.DownloadAllAIPromptsForAgentAsync(It.IsAny<AgentSyncInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AIPromptMetadata>());
        return mockDataverse;
    }

    private static string ReadFileText(IFileAccessor accessor, string path)
    {
        using var stream = accessor.OpenRead(new AgentFilePath(path));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static FileAttachmentComponent CreateKnowledgeFileComponent(string schemaName, string displayName, BotComponentId parentId, Guid? id = null)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription(string.Empty)
            .ToBuilder();
        builder.Id = id ?? Guid.NewGuid();
        builder.ParentBotComponentId = parentId;
        return builder.Build();
    }

    private static void AssertNoChildAgentCreateOrDelete(System.Collections.Generic.IEnumerable<Change> changes)
    {
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Create && c.SchemaName.Contains(".agent."));
        Assert.DoesNotContain(changes, c => c.ChangeType == ChangeType.Delete && c.SchemaName.Contains(".agent."));
    }

    private static Change SingleChildAgentChange(System.Collections.Generic.IEnumerable<Change> changes, ChangeType changeType)
        => Assert.Single(changes.Where(c => c.ChangeType == changeType && c.SchemaName.Contains(".agent.")));

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
        => WriteText(accessor, path, "kind: AgentDialog\nbeginDialog:\n  kind: OnToolSelected\n  id: main\n");

    private static void WriteText(IFileAccessor accessor, string path, string contents)
    {
        using var stream = accessor.OpenWrite(new AgentFilePath(path));
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(contents);
    }

    private sealed class OrderedFileAccessor : IFileAccessor
    {
        private readonly IFileAccessor _inner;
        private readonly IReadOnlyList<string> _folderOrder;

        public OrderedFileAccessor(IFileAccessor inner, IReadOnlyList<string> folderOrder)
        {
            _inner = inner;
            _folderOrder = folderOrder;
        }

        public bool Exists(AgentFilePath path) => _inner.Exists(path);

        public void CreateHiddenDirectory(AgentFilePath path) => _inner.CreateHiddenDirectory(path);

        public Stream OpenWrite(AgentFilePath path) => _inner.OpenWrite(path);

        public Stream OpenRead(AgentFilePath path) => _inner.OpenRead(path);

        public void Delete(AgentFilePath path) => _inner.Delete(path);

        public void DeleteDirectory(AgentFilePath path) => _inner.DeleteDirectory(path);

        public void Replace(AgentFilePath sourcePath, AgentFilePath targetPath) => _inner.Replace(sourcePath, targetPath);

        public IEnumerable<AgentFilePath> ListFiles(string? relativeFolder = null, string filePattern = "*.*")
            => _inner.ListFiles(relativeFolder, filePattern).OrderBy(RankOf).ToList();

        private int RankOf(AgentFilePath path)
        {
            for (var index = 0; index < _folderOrder.Count; index++)
            {
                if (path.ToString().StartsWith(_folderOrder[index], StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return _folderOrder.Count;
        }
    }
}
