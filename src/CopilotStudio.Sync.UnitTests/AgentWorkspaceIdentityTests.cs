// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Moq;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AgentWorkspaceIdentityTests
{
    private const string SettingsFile = "settings.mcs.yml";
    private const string AgentFile = "agent.mcs.yml";

    public static TheoryData<string, AgentScaffoldFailure> NamesAnAgentCannotTake => new()
    {
        { "!!!", AgentScaffoldFailure.DisplayNameUnusable },
        { "-- . --", AgentScaffoldFailure.DisplayNameUnusable },
        { "Contoso\tCopy", AgentScaffoldFailure.DisplayNameUnusable },
        { "Contoso\nCopy", AgentScaffoldFailure.DisplayNameUnusable },
        { new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength + 1), AgentScaffoldFailure.DisplayNameTooLong },
    };

    public static TheoryData<string?> NamesReturnedUnchanged => new() { null, "", "   ", new string(' ', AgentWorkspaceIdentity.MaximumDisplayNameLength + 1), "Contoso Copy", "7", "Zürich Helpdesk", new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength), " " + new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength - 2) + " " };

    public static TheoryData<string> NamesLongerThanTheLimit => new() { new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength + 1), new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength) + " ", " " + new string('a', AgentWorkspaceIdentity.MaximumDisplayNameLength), new string('!', AgentWorkspaceIdentity.MaximumDisplayNameLength + 1) };

    [Fact]
    public async Task TryRead_ReadsQuotedNamesAndDescriptionFromTheSettingsFile()
    {
        Assert.Equal(new AgentWorkspaceIdentity("con_helpdesk", "Contoso Helpdesk", "From the source") { DeclaredDisplayName = "Contoso Helpdesk" }, AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, "displayName: \"Contoso Helpdesk\"\nschemaName: \"con_helpdesk\"\ndescription: \"From the source\"\n"))));
    }

    [Fact]
    public async Task TryRead_ReadsUnquotedScalars()
    {
        Assert.Equal(new AgentWorkspaceIdentity("con_helpdesk", "Contoso Helpdesk", "Routes tickets") { DeclaredDisplayName = "Contoso Helpdesk" }, AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, "displayName: Contoso Helpdesk\nschemaName: con_helpdesk\ndescription: Routes tickets\n"))));
    }

    [Fact]
    public async Task TryRead_ReadsTheSettingsFileOfAClonedClassicAgent()
    {
        Assert.Equal(new AgentWorkspaceIdentity("cr5a1_classic", "Classic Agent", null) { DeclaredDisplayName = "Classic Agent" }, AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, "kind: Bot\ndisplayName: Classic Agent\nschemaName: cr5a1_classic\ntemplate: default-2.1.0\nlanguage: 1033\nconfiguration:\n  gPTSettings:\n    defaultSchemaName: cr5a1_classic.gpt.default\n"), (AgentFile, "kind: GptComponentMetadata\ninstructions: Help people.\n"))));
    }

    [Fact]
    public async Task TryRead_WithNoSettingsFile_ReadsTheAgentFile()
    {
        Assert.Equal(new AgentWorkspaceIdentity("con_classic", "Classic Agent", null) { DeclaredDisplayName = "Classic Agent" }, AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((AgentFile, "displayName: \"Classic Agent\"\nschemaName: \"con_classic\"\n"))));
    }

    [Fact]
    public async Task TryRead_WithASettingsFileThatNamesNoSchema_DoesNotFallBackToTheAgentFile()
    {
        Assert.Null(AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, "displayName: Contoso Helpdesk\n"), (AgentFile, "schemaName: con_classic\n"))));
    }

    [Fact]
    public async Task TryRead_WithAnAgentFileThatHoldsOnlyInstructions_ReturnsNull()
    {
        Assert.Null(AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((AgentFile, "kind: GptComponentMetadata\ndisplayName: Helper\ndescription: Helps.\ninstructions: Help people.\n"))));
    }

    [Fact]
    public async Task TryRead_UnescapesAQuotedDisplayName()
    {
        Assert.Equal("He said \"hi\"", AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, "displayName: \"He said \\\"hi\\\"\"\nschemaName: \"con_agent\"\n")))!.DisplayName);
    }

    [Theory]
    [InlineData("schemaName: con_agent\n")]
    [InlineData("displayName:\nschemaName: con_agent\n")]
    [InlineData("displayName: \"\"\nschemaName: con_agent\n")]
    public async Task TryRead_WithNoDisplayName_FallsBackToTheSchemaName(string settings)
    {
        Assert.Equal(new AgentWorkspaceIdentity("con_agent", "con_agent", null), AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, settings))));
    }

    [Fact]
    public async Task TryRead_IgnoresNestedKeysOfTheSameNames()
    {
        Assert.Equal(new AgentWorkspaceIdentity("con_agent", "con_agent", null), AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, "schemaName: con_agent\nconfiguration:\n  gPTSettings:\n    defaultSchemaName: con_agent.gpt.default\n    schemaName: con_nested\n    description: Nested\n"))));
    }

    [Theory]
    [InlineData("description: 42\nschemaName: con_agent\n")]
    [InlineData("description: true\nschemaName: con_agent\n")]
    [InlineData("description:\nschemaName: con_agent\n")]
    [InlineData("description: \"\"\nschemaName: con_agent\n")]
    [InlineData("Description: Wrong case\nschemaName: con_agent\n")]
    public async Task TryRead_WithNoTextDescription_ReturnsNoDescription(string settings)
    {
        Assert.Null(AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, settings)))!.Description);
    }

    [Fact]
    public async Task TryRead_ReadsABlockDescription()
    {
        Assert.Equal("Line one\nLine two\n", AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, "schemaName: con_agent\ndescription: |\n  Line one\n  Line two\n")))!.Description);
    }

    [Theory]
    [InlineData("schemaName: con_agent # the agent\n", "con_agent")]
    [InlineData("schemaName: 'it''s'\n", "it's")]
    [InlineData("displayName: x\r\nschemaName: con_crlf\r\n", "con_crlf")]
    [InlineData("\uFEFFschemaName: con_bom\n", "con_bom")]
    public async Task TryRead_ReadsYamlScalarsAsTheObjectModelDoes(string settings, string expectedSchemaName)
    {
        Assert.Equal(expectedSchemaName, AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, settings)))!.SchemaName);
    }

    [Fact]
    public async Task TryRead_WithNoIdentityFile_ReturnsNull()
    {
        Assert.Null(AgentWorkspaceIdentity.TryRead(await WorkspaceAsync(("topics/greeting.mcs.yml", "kind: AdaptiveDialog\n"))));
    }

    [Theory]
    [InlineData("displayName: Contoso Helpdesk\n")]
    [InlineData("schemaName: \"\"\ndisplayName: Contoso Helpdesk\n")]
    [InlineData("schemaName: \"   \"\n")]
    [InlineData("")]
    public async Task TryRead_WithNoSchemaName_ReturnsNull(string settings)
    {
        Assert.Null(AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, settings))));
    }

    [Theory]
    [InlineData("schemaName: a\nschemaName: b\n")]
    [InlineData("<<<<<<< HEAD\nschemaName: ours\n=======\nschemaName: theirs\n>>>>>>> branch\n")]
    [InlineData("schemaName: \"con_agent\ndisplayName: Unterminated\n")]
    [InlineData("schemaName: con_agent\nconfiguration:\n\tgPTSettings: {}\n")]
    public async Task TryRead_WithMalformedYaml_ReturnsNull(string settings)
    {
        Assert.Null(AgentWorkspaceIdentity.TryRead(await WorkspaceAsync((SettingsFile, settings))));
    }

    [Fact]
    public void TryRead_WithAnUnreadableFile_ReturnsNull()
    {
        var workspace = new Mock<IFileAccessor>();
        workspace.Setup(accessor => accessor.Exists(It.IsAny<AgentFilePath>())).Returns(true);
        workspace.Setup(accessor => accessor.OpenRead(It.IsAny<AgentFilePath>())).Throws(new IOException("The file is locked."));

        Assert.Null(AgentWorkspaceIdentity.TryRead(workspace.Object));
    }

    [Fact]
    public void TryRead_WithNoAccessor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AgentWorkspaceIdentity.TryRead(null!));
    }

    [Theory]
    [InlineData("displayName: Contoso Helpdesk\n")]
    [InlineData("displayName: \"Contoso Helpdesk\"\n")]
    [InlineData("displayName: 'Contoso ''Helpdesk'''\n")]
    [InlineData("\"displayName\": Contoso Helpdesk\n")]
    [InlineData("displayName : Contoso Helpdesk # the source\n")]
    [InlineData("displayName:\n")]
    [InlineData("displayName:\n  Contoso Helpdesk\n")]
    [InlineData("displayName: |-\n  Contoso\n\n  Helpdesk\n")]
    [InlineData("displayName: >\n  Contoso\n  Helpdesk\n")]
    public async Task DeclareDisplayName_ReplacesTheDeclaredNameWhateverItsStyleAndKeepsEveryOtherLine(string declared)
    {
        var workspace = await WorkspaceAsync((SettingsFile, declared + "schemaName: con_helpdesk\ndescription: Routes tickets\nconfiguration:\n  displayName: Nested\n"));

        AgentWorkspaceIdentity.DeclareDisplayName(workspace, "Contoso Copy");

        Assert.Equal("displayName: \"Contoso Copy\"\nschemaName: con_helpdesk\ndescription: Routes tickets\nconfiguration:\n  displayName: Nested\n", await workspace.ReadStringAsync(new AgentFilePath(SettingsFile), CancellationToken.None));
        Assert.Equal(new AgentWorkspaceIdentity("con_helpdesk", "Contoso Copy", "Routes tickets") { DeclaredDisplayName = "Contoso Copy" }, AgentWorkspaceIdentity.TryRead(workspace));
    }

    [Theory]
    [InlineData("schemaName: con_agent\nconfiguration:\n  displayName: Nested\n", "displayName: \"Contoso Copy\"\nschemaName: con_agent\nconfiguration:\n  displayName: Nested\n")]
    [InlineData("schemaName: con_agent\r\nlanguage: 1033\r\n", "displayName: \"Contoso Copy\"\r\nschemaName: con_agent\r\nlanguage: 1033\r\n")]
    [InlineData("displayName: Contoso Helpdesk\r\nschemaName: con_agent\r\n", "displayName: \"Contoso Copy\"\r\nschemaName: con_agent\r\n")]
    public async Task DeclareDisplayName_AddsANameTheFileLacksAtItsTopAndKeepsItsLineBreaks(string settings, string expected)
    {
        var workspace = await WorkspaceAsync((SettingsFile, settings));

        AgentWorkspaceIdentity.DeclareDisplayName(workspace, "Contoso Copy");

        Assert.Equal(expected, await workspace.ReadStringAsync(new AgentFilePath(SettingsFile), CancellationToken.None));
        Assert.Equal("Contoso Copy", AgentWorkspaceIdentity.TryRead(workspace)!.DeclaredDisplayName);
    }

    [Fact]
    public async Task DeclareDisplayName_KeepsTheFilesByteOrderMark()
    {
        var workspace = await WorkspaceAsync((SettingsFile, "\uFEFFdisplayName: Contoso Helpdesk\nschemaName: con_agent\n"));

        AgentWorkspaceIdentity.DeclareDisplayName(workspace, "Contoso Copy");

        Assert.Equal(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("displayName: \"Contoso Copy\"\nschemaName: con_agent\n")), workspace.ReadBytes(new AgentFilePath(SettingsFile)).ToArray());
    }

    [Theory]
    [InlineData("He said \"hi\" \\o/")]
    [InlineData("#1: Contoso's 'Helpdesk' & Co")]
    [InlineData("  Padded  ")]
    public async Task DeclareDisplayName_WritesANameTryReadReturnsAsGiven(string displayName)
    {
        var workspace = await WorkspaceAsync((SettingsFile, "displayName: Contoso Helpdesk\nschemaName: con_agent\n"));

        AgentWorkspaceIdentity.DeclareDisplayName(workspace, displayName);

        Assert.Equal(displayName, AgentWorkspaceIdentity.TryRead(workspace)!.DeclaredDisplayName);
    }

    [Fact]
    public async Task DeclareDisplayName_WithNoSettingsFile_DeclaresTheNameInTheAgentFile()
    {
        var workspace = await WorkspaceAsync((AgentFile, "displayName: \"Classic Agent\"\nschemaName: \"con_classic\"\n"));

        AgentWorkspaceIdentity.DeclareDisplayName(workspace, "Classic Copy");

        Assert.False(workspace.Exists(new AgentFilePath(SettingsFile)));
        Assert.Equal(new AgentWorkspaceIdentity("con_classic", "Classic Copy", null) { DeclaredDisplayName = "Classic Copy" }, AgentWorkspaceIdentity.TryRead(workspace));
    }

    [Fact]
    public async Task DeclareDisplayName_WithASettingsFile_LeavesTheAgentFileAsItIs()
    {
        const string agent = "kind: GptComponentMetadata\ndisplayName: Classic Agent\ninstructions: Help people.\n";
        var workspace = await WorkspaceAsync((SettingsFile, "displayName: Classic Agent\nschemaName: cr5a1_classic\n"), (AgentFile, agent));

        AgentWorkspaceIdentity.DeclareDisplayName(workspace, "Classic Copy");

        Assert.Equal(agent, await workspace.ReadStringAsync(new AgentFilePath(AgentFile), CancellationToken.None));
        Assert.Equal("Classic Copy", AgentWorkspaceIdentity.TryRead(workspace)!.DeclaredDisplayName);
    }

    [Fact]
    public void DeclareDisplayName_OfTheNameTheFileAlreadyDeclares_WritesNothing()
    {
        var workspace = new Mock<IFileAccessor>();
        workspace.Setup(accessor => accessor.Exists(It.IsAny<AgentFilePath>())).Returns(true);
        workspace.Setup(accessor => accessor.OpenRead(It.IsAny<AgentFilePath>())).Returns(() => new MemoryStream(Encoding.UTF8.GetBytes("displayName: \"Contoso Helpdesk\"\nschemaName: con_helpdesk\n")));

        AgentWorkspaceIdentity.DeclareDisplayName(workspace.Object, "Contoso Helpdesk");

        workspace.Verify(accessor => accessor.OpenWrite(It.IsAny<AgentFilePath>()), Times.Never());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DeclareDisplayName_WithNoName_TouchesNothing(string? displayName)
    {
        var workspace = new Mock<IFileAccessor>(MockBehavior.Strict);

        AgentWorkspaceIdentity.DeclareDisplayName(workspace.Object, displayName);

        workspace.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(NamesAnAgentCannotTake))]
    public async Task DeclareDisplayName_OfANameAnAgentCannotTake_ThrowsAndLeavesTheFileAsItIs(string displayName, AgentScaffoldFailure expected)
    {
        const string settings = "displayName: Contoso Helpdesk\nschemaName: con_helpdesk\n";
        var workspace = await WorkspaceAsync((SettingsFile, settings));

        var failure = Assert.Throws<AgentScaffoldException>(() => AgentWorkspaceIdentity.DeclareDisplayName(workspace, displayName));

        Assert.Equal((expected, displayName), (failure.Failure, failure.Subject));
        Assert.Equal(settings, await workspace.ReadStringAsync(new AgentFilePath(SettingsFile), CancellationToken.None));
    }

    [Fact]
    public async Task DeclareDisplayName_WithNoIdentityFile_Throws()
    {
        var workspace = await WorkspaceAsync(("topics/greeting.mcs.yml", "kind: AdaptiveDialog\n"));

        Assert.Throws<InvalidOperationException>(() => AgentWorkspaceIdentity.DeclareDisplayName(workspace, "Contoso Copy"));
        Assert.Equal(new[] { "topics/greeting.mcs.yml" }, workspace.ListFiles().Select(path => path.ToString()));
    }

    [Fact]
    public void DeclareDisplayName_WithNoAccessor_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AgentWorkspaceIdentity.DeclareDisplayName(null!, "Contoso Copy"));
    }

    [Theory]
    [MemberData(nameof(NamesReturnedUnchanged))]
    public void ValidateDisplayName_ReturnsNoNameOrANameAnAgentCanTakeUnchanged(string? displayName)
    {
        Assert.Same(displayName, AgentWorkspaceIdentity.ValidateDisplayName(displayName));
    }

    [Theory]
    [MemberData(nameof(NamesLongerThanTheLimit))]
    public void ValidateDisplayName_OfANameLongerThanTheLimitCountingItsSpaces_RefusesItNamingTheLimitAndItsLength(string displayName)
    {
        var failure = Assert.Throws<AgentScaffoldException>(() => AgentWorkspaceIdentity.ValidateDisplayName(displayName));

        Assert.Equal((AgentScaffoldFailure.DisplayNameTooLong, displayName), (failure.Failure, failure.Subject));
        Assert.Equal($"An agent display name can hold at most 42 characters, and this one holds {displayName.Length}.", failure.Message);
    }

    private static async Task<IFileAccessor> WorkspaceAsync(params (string Path, string Content)[] files)
    {
        var workspace = new InMemoryFileAccessor(new DirectoryPath("c:/workspace/"));
        foreach (var (path, content) in files)
        {
            await workspace.WriteAsync(new AgentFilePath(path), new UTF8Encoding(false).GetBytes(content), CancellationToken.None);
        }

        return workspace;
    }
}