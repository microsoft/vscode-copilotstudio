// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.Agents.ObjectModel;
using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

/// <summary>
/// Verifies that sub-agents (<see cref="AgentDialog"/>) and their child components
/// project to a folder derived from the sub-agent's <b>display name</b> rather than the
/// machine-generated schema short-name. Cloud-authored sub-agents get schema names like
/// <c>{bot}.agent.Agent_7_8</c>; previously they landed in <c>agents/Agent_7_8/</c>, which
/// is unfriendly. They should land in <c>agents/&lt;DisplayName&gt;/</c> instead.
/// </summary>
public class SubAgentDisplayNameProjectionTests
{
    private const string Bot = "crd1c_agent";

    [Fact]
    public void GetComponentPath_SubAgentDialog_UsesDisplayNameFolder()
    {
        var resolver = new LspComponentPathResolver();
        var agent = CreateAgentDialogComponent($"{Bot}.agent.Agent_7_8", "Transfer Funds", Guid.NewGuid());
        var definition = CreateDefinition(new BotComponentBase[] { agent });

        var path = resolver.GetComponentPath(agent, definition).Replace('\\', '/');

        Assert.Equal("agents/TransferFunds/agent.mcs.yml", path);
    }

    [Fact]
    public void GetComponentPath_SubAgentChild_UsesParentDisplayNameFolder()
    {
        var resolver = new LspComponentPathResolver();
        var agentId = Guid.NewGuid();
        var agent = CreateAgentDialogComponent($"{Bot}.agent.Agent_7_8", "Transfer Funds", agentId);
        var childTopic = CreateTopicComponent($"{Bot}.topic.ChildTopic", Guid.NewGuid(), new BotComponentId(agentId));
        var definition = CreateDefinition(new BotComponentBase[] { agent, childTopic });

        var path = resolver.GetComponentPath(childTopic, definition).Replace('\\', '/');

        Assert.StartsWith("agents/TransferFunds/", path);
        Assert.EndsWith(".mcs.yml", path);
    }

    [Fact]
    public void GetComponentPath_SubAgentChildFile_UsesParentDisplayNameFolder()
    {
        var resolver = new LspComponentPathResolver();
        var agentId = Guid.NewGuid();
        var agent = CreateAgentDialogComponent($"{Bot}.agent.Agent_2qD", "Lost or Stolen Card", agentId);
        var childFile = CreateFileComponent($"{Bot}.file.ChildFile", "ChildFile", new BotComponentId(agentId));
        var definition = CreateDefinition(new BotComponentBase[] { agent, childFile });

        var path = resolver.GetComponentPath(childFile, definition).Replace('\\', '/');

        Assert.StartsWith("agents/LostorStolenCard/knowledge/files/", path);
    }

    [Fact]
    public void GetComponentPath_SubAgentDialog_BlankDisplayName_FallsBackToSchemaShortName()
    {
        var resolver = new LspComponentPathResolver();
        var agent = CreateAgentDialogComponent($"{Bot}.agent.Agent_7_8", "   ", Guid.NewGuid());
        var definition = CreateDefinition(new BotComponentBase[] { agent });

        var path = resolver.GetComponentPath(agent, definition).Replace('\\', '/');

        // No usable display name -> preserve today's schema-derived projection.
        Assert.Equal("agents/Agent_7_8/agent.mcs.yml", path);
    }

    [Theory]
    [InlineData("Transfer Funds", "TransferFunds")]
    [InlineData("Helper Agent", "HelperAgent")]
    [InlineData("Lost or Stolen Card", "LostorStolenCard")]
    [InlineData("  Balance Agent  ", "BalanceAgent")]
    [InlineData("my-agent_v2", "my-agent_v2")]
    [InlineData("A/B:C", "ABC")]
    [InlineData("agent.v1", "agentv1")]
    [InlineData("Bad<>:\"/\\|?*Chars", "BadChars")]
    public void FromDisplayName_StripsWhitespaceAndPunctuation(string displayName, string expected)
    {
        Assert.Equal(expected, SubAgentFolderNaming.FromDisplayName(displayName));
    }

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("con", "con_")]
    [InlineData("NUL", "NUL_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("LPT9", "LPT9_")]
    [InlineData("C.O.N", "CON_")] // dots stripped first, then reserved
    public void FromDisplayName_DisambiguatesWindowsReservedNames(string displayName, string expected)
    {
        Assert.Equal(expected, SubAgentFolderNaming.FromDisplayName(displayName));
    }

    [Theory]
    [InlineData("Console")]
    [InlineData("Control")]
    [InlineData("COM")]
    [InlineData("COM10")]
    [InlineData("Companion")]
    public void FromDisplayName_DoesNotAlterNamesThatMerelyResembleReservedNames(string displayName)
    {
        // Only exact reserved names are disambiguated.
        Assert.Equal(displayName, SubAgentFolderNaming.FromDisplayName(displayName));
    }

    [Fact]
    public void FromDisplayName_StripsControlCharacters()
    {
        Assert.Equal("AB", SubAgentFolderNaming.FromDisplayName("A\t\n\u0007\u0085B"));
    }

    [Fact]
    public void FromDisplayName_CapsLengthForFilesystemComponentLimits()
    {
        var result = SubAgentFolderNaming.FromDisplayName(new string('a', 500));
        Assert.NotNull(result);
        Assert.Equal(100, result!.Length);
    }

    [Fact]
    public void FromDisplayName_NeverEndsInDotOrSpace()
    {
        // Windows silently trims trailing dots/spaces; ensure the sanitized name has none.
        var result = SubAgentFolderNaming.FromDisplayName("My Agent. ");
        Assert.Equal("MyAgent", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("<>:\"/\\|?*")]
    public void FromDisplayName_ReturnsNull_WhenNoUsableCharacters(string? displayName)
    {
        Assert.Null(SubAgentFolderNaming.FromDisplayName(displayName));
    }

    [Fact]
    public void FromDisplayName_PreservesNonAsciiLetters()
    {
        // Non-ASCII letters are kept (consistent with the root-agent folder sanitizer),
        // so localized sub-agent names survive whitespace stripping.
        Assert.Equal("日本語Agent", SubAgentFolderNaming.FromDisplayName("日本語 Agent"));
    }

    private static BotDefinition CreateDefinition(IEnumerable<BotComponentBase> components)
    {
        var botEntity = CodeSerializer.Deserialize<BotEntity>($"kind: Bot\nschemaName: {Bot}")!;
        return new BotDefinition().WithEntity(botEntity).WithComponents(components);
    }

    private static DialogComponent CreateAgentDialogComponent(string schemaName, string displayName, Guid id)
    {
        return new DialogComponent(
            schemaName: schemaName,
            displayName: displayName,
            description: string.Empty,
            id: id,
            parentBotComponentId: default,
            dialog: new AgentDialog());
    }

    private static DialogComponent CreateTopicComponent(string schemaName, Guid id, BotComponentId parentId)
    {
        return new DialogComponent(
            schemaName: schemaName,
            displayName: schemaName.Split('.').Last(),
            description: string.Empty,
            id: id,
            parentBotComponentId: parentId,
            dialog: new AdaptiveDialog());
    }

    private static FileAttachmentComponent CreateFileComponent(string schemaName, string displayName, BotComponentId parentId)
    {
        var builder = new FileAttachmentComponent()
            .WithSchemaName(schemaName)
            .WithDisplayName(displayName)
            .WithDescription("desc")
            .ToBuilder();
        builder.Id = Guid.NewGuid();
        builder.ParentBotComponentId = parentId;
        return builder.Build();
    }
}
