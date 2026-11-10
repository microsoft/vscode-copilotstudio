// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Hosting;
using Xunit;
using static Microsoft.CopilotStudio.Sync.UnitTests.AgentWorkspaceHostPushTests;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentWorkspaceHostSnapshotTests
{
    private const string References = "Agent/references.mcs.yml";

    private readonly HostedCloud _cloud = new();

    private readonly RecordingFileAccessorFactory _files = new();

    private readonly AgentWorkspaceHost _host;

    public AgentWorkspaceHostSnapshotTests() => _host = _cloud.CreateHost(_files);

    [Theory]
    [InlineData(AgentAuthoringModes.Classic)]
    [InlineData(AgentAuthoringModes.CliCopilot)]
    public async Task ValidateAsync_OfAScaffold_IsValidAndTellsTheAuthoringMode(string authoringMode)
    {
        var validation = await _host.ValidateAsync(Scaffold(authoringMode), CancellationToken.None);

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Diagnostics);
        Assert.Empty(validation.DeclaredCollectionFolders);
        Assert.Empty(validation.DeclaredCollections);
        Assert.Equal(authoringMode, validation.AuthoringMode);
    }

    [Fact]
    public async Task ValidateAsync_OfAClassicAgentWithCollections_ReportsTheDeclaredFoldersInOrder()
    {
        var validation = await _host.ValidateAsync(ScaffoldWithCollections(), CancellationToken.None);

        Assert.True(validation.IsValid);
        Assert.Equal(new[] { "Shared Tools", "Zeta Tools" }, validation.DeclaredCollectionFolders);
    }

    [Fact]
    public async Task ValidateAsync_OfAClassicAgentWithCollections_MapsEachDeclaredSchemaNameToItsFolderIgnoringCase()
    {
        var validation = await _host.ValidateAsync(ScaffoldWithCollections(), CancellationToken.None);

        Assert.Equal(new[] { new KeyValuePair<string, string>("cree9_tools", "Shared Tools"), new KeyValuePair<string, string>("cree9_zeta", "Zeta Tools") }, validation.DeclaredCollections);
        Assert.Equal("Zeta Tools", validation.DeclaredCollections["CREE9_ZETA"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("displayName: Shared Tools\n")]
    [InlineData("schemaName: \"  \"\n")]
    [InlineData("kind: AdaptiveDialog\nbeginDialog: x\n")]
    public async Task ValidateAsync_OfACollectionProjectThatNamesNoSchemaName_ReportsItAndMapsOnlyTheOtherFolders(string collectionProject)
    {
        var validation = await _host.ValidateAsync(With(ScaffoldWithCollections(), "Shared Tools/collection.mcs.yml", collectionProject), CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.Equal(new (string, string?)[] { (AgentSyncDiagnosticCodes.CollectionSchemaNameMissing, "Shared Tools/collection.mcs.yml") }, Findings(validation));
        Assert.Equal(new[] { new KeyValuePair<string, string>("cree9_zeta", "Zeta Tools") }, validation.DeclaredCollections);
    }

    [Fact]
    public async Task ValidateAsync_OfTwoCollectionFoldersDeclaringOneSchemaNameIgnoringCase_ReportsTheLaterFolder()
    {
        var validation = await _host.ValidateAsync(With(ScaffoldWithCollections(), "Zeta Tools/collection.mcs.yml", "schemaName: CREE9_TOOLS\ndisplayName: Zeta Tools\n"), CancellationToken.None);
        var problem = Assert.Single(validation.Diagnostics);

        Assert.False(validation.IsValid);
        Assert.Equal((AgentSyncDiagnosticCodes.CollectionSchemaNameRepeated, "Zeta Tools/collection.mcs.yml", "CREE9_TOOLS"), (problem.Code, problem.Path, problem.Subject));
        Assert.Contains("'Shared Tools' and 'Zeta Tools'", problem.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { new KeyValuePair<string, string>("cree9_tools", "Shared Tools") }, validation.DeclaredCollections);
    }

    [Theory]
    [InlineData("/Agent/topics/Extra.mcs.yml", AgentSyncDiagnosticCodes.AbsolutePath)]
    [InlineData("C:/Agent/topics/Extra.mcs.yml", AgentSyncDiagnosticCodes.AbsolutePath)]
    [InlineData("Agent/../Other/Extra.mcs.yml", AgentSyncDiagnosticCodes.RelativeSegmentInPath)]
    [InlineData("Agent/./topics/Extra.mcs.yml", AgentSyncDiagnosticCodes.RelativeSegmentInPath)]
    [InlineData("Agent//Extra.mcs.yml", AgentSyncDiagnosticCodes.RelativeSegmentInPath)]
    [InlineData("Agent\\topics\\Extra.mcs.yml", AgentSyncDiagnosticCodes.BackslashInPath)]
    [InlineData("Agent/topics/GREETING.mcs.yml", AgentSyncDiagnosticCodes.CaseOnlyDuplicate)]
    [InlineData("agent/settings.mcs.yml", AgentSyncDiagnosticCodes.CaseOnlyDuplicate)]
    [InlineData("Agent/.mcs/botdefinition.json", AgentSyncDiagnosticCodes.SyncStateInPath)]
    [InlineData("Agent/topics/.Mcs/changetoken.txt", AgentSyncDiagnosticCodes.SyncStateInPath)]
    [InlineData(".MCS/botdefinition.json", AgentSyncDiagnosticCodes.SyncStateInPath)]
    [InlineData("Other/topics/Extra.mcs.yml", AgentSyncDiagnosticCodes.OutsideDeclaredFolders)]
    [InlineData("Extra.mcs.yml", AgentSyncDiagnosticCodes.OutsideDeclaredFolders)]
    [InlineData("Agent/topics/PRN", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData("Agent/topics/nul.mcs.yml", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData("Agent/LPT\u00B3 ./Extra.mcs.yml", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData("Agent/topics/Extra.mcs.yml.", AgentSyncDiagnosticCodes.UnsafePath)]
    [InlineData("Agent/topics /Extra.mcs.yml", AgentSyncDiagnosticCodes.UnsafePath)]
    [InlineData("Agent/topics/Ex:tra.mcs.yml", AgentSyncDiagnosticCodes.UnsafePath)]
    public async Task ValidateAsync_WithAPathAPushMustNotWrite_ReportsIt(string path, string code)
    {
        var validation = await _host.ValidateAsync(With(Scaffold(AgentAuthoringModes.Classic), path, Topic("Extra")), CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.Equal(code, Assert.Single(validation.Diagnostics).Code);
    }

    [Fact]
    public async Task ValidateAsync_ReportsEveryProblemInPathOrder()
    {
        Assert.Equal(new (string, string?)[] { (AgentSyncDiagnosticCodes.SyncStateInPath, "Agent/.mcs/changetoken.txt"), (AgentSyncDiagnosticCodes.BackslashInPath, "Agent\\Extra.mcs.yml"), (AgentSyncDiagnosticCodes.OutsideDeclaredFolders, "Other/") }, Findings(await _host.ValidateAsync(With(With(With(Scaffold(AgentAuthoringModes.Classic), "Agent/.mcs/changetoken.txt", "token"), "Agent\\Extra.mcs.yml", Topic("Extra")), "Other/Extra.mcs.yml", Topic("Extra")), CancellationToken.None)));
    }

    [Fact]
    public async Task ValidateAsync_OfAnEmptySnapshot_ReportsIt()
    {
        var validation = await _host.ValidateAsync(new AgentWorkspaceSnapshot("Agent", new Dictionary<string, ReadOnlyMemory<byte>>()), CancellationToken.None);

        Assert.False(validation.IsValid);
        Assert.Equal(AgentSyncDiagnosticCodes.EmptySnapshot, Assert.Single(validation.Diagnostics).Code);
        Assert.Null(validation.AuthoringMode);
    }

    [Theory]
    [InlineData("Agent/Nested", AgentSyncDiagnosticCodes.FolderInvalid)]
    [InlineData("Agent\\Nested", AgentSyncDiagnosticCodes.FolderInvalid)]
    [InlineData("..", AgentSyncDiagnosticCodes.FolderInvalid)]
    [InlineData(".mcs", AgentSyncDiagnosticCodes.FolderInvalid)]
    [InlineData(" ", AgentSyncDiagnosticCodes.FolderInvalid)]
    [InlineData("Com7", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData("AUX.bot", AgentSyncDiagnosticCodes.ReservedDeviceName)]
    [InlineData(" Agent", AgentSyncDiagnosticCodes.UnsafePath)]
    [InlineData("Agent>", AgentSyncDiagnosticCodes.UnsafePath)]
    public async Task ValidateAsync_OfASnapshotWhoseFolderIsNotATopLevelFolder_ReportsIt(string folder, string code)
    {
        var validation = await _host.ValidateAsync(Scaffold(AgentAuthoringModes.Classic) with { Folder = folder }, CancellationToken.None);

        Assert.Equal((code, folder), (Assert.Single(validation.Diagnostics).Code, validation.Diagnostics[0].Path));
    }

    [Fact]
    public async Task ValidateAsync_OfAFolderWithoutSettings_ReportsTheMissingAgentProject()
    {
        var validation = await _host.ValidateAsync(With(Scaffold(AgentAuthoringModes.Classic), "Agent/settings.mcs.yml", null), CancellationToken.None);

        Assert.Equal((AgentSyncDiagnosticCodes.AgentProjectMissing, "Agent/settings.mcs.yml"), (Assert.Single(validation.Diagnostics).Code, validation.Diagnostics[0].Path));
    }

    [Theory]
    [InlineData("Shared Tools/", AgentSyncDiagnosticCodes.CollectionDeclarationInvalid)]
    [InlineData("../Agent/", AgentSyncDiagnosticCodes.CollectionDeclarationInvalid)]
    [InlineData("../Shared Tools/Nested/", AgentSyncDiagnosticCodes.CollectionDeclarationInvalid)]
    [InlineData("../.mcs/", AgentSyncDiagnosticCodes.CollectionDeclarationInvalid)]
    [InlineData("C:/Shared Tools/", AgentSyncDiagnosticCodes.CollectionDeclarationInvalid)]
    [InlineData("../CON/", AgentSyncDiagnosticCodes.CollectionDeclarationInvalid)]
    [InlineData("../Shared Tools /", AgentSyncDiagnosticCodes.CollectionDeclarationInvalid)]
    [InlineData("../Missing/", AgentSyncDiagnosticCodes.CollectionFolderMissing)]
    public async Task ValidateAsync_OfACollectionDeclaredAnywhereButAPresentSiblingFolder_ReportsIt(string directory, string code)
    {
        var validation = await _host.ValidateAsync(With(Scaffold(AgentAuthoringModes.Classic), References, $"componentCollections:\n  - schemaName: cree9_tools\n    directory: {directory}\n"), CancellationToken.None);

        Assert.Equal(code, Assert.Single(validation.Diagnostics).Code);
        Assert.False(validation.IsValid);
    }

    [Fact]
    public async Task ValidateAsync_OfACollectionFolderWithoutItsProjectOrHoldingAnAgent_ReportsBoth()
    {
        Assert.Equal(new (string, string?)[] { (AgentSyncDiagnosticCodes.CollectionProjectMissing, "Shared Tools/collection.mcs.yml"), (AgentSyncDiagnosticCodes.CollectionHoldsAgent, "Zeta Tools/agent.mcs.yml") }, Findings(await _host.ValidateAsync(With(With(ScaffoldWithCollections(), "Shared Tools/collection.mcs.yml", null), "Zeta Tools/agent.mcs.yml", "kind: GptComponentMetadata\n"), CancellationToken.None)));
    }

    [Fact]
    public async Task ValidateAsync_OfMalformedReferences_ReportsTheCollectionFoldersTheyNoLongerDeclare()
    {
        Assert.Equal(new (string, string?)[] { (AgentSyncDiagnosticCodes.OutsideDeclaredFolders, "Shared Tools/"), (AgentSyncDiagnosticCodes.OutsideDeclaredFolders, "Zeta Tools/") }, Findings(await _host.ValidateAsync(With(ScaffoldWithCollections(), References, "componentCollections: [unclosed\n"), CancellationToken.None)));
    }

    [Fact]
    public async Task ValidateAsync_OfACliAgentDeclaringACollection_ReportsIt()
    {
        var validation = await _host.ValidateAsync(new AgentWorkspaceSnapshot("Agent", (Scaffold(AgentAuthoringModes.CliCopilot)).Files.Concat((ScaffoldWithCollections()).Files.Where(file => !file.Key.StartsWith("Agent/", StringComparison.Ordinal) || file.Key == References)).ToDictionary(file => file.Key, file => file.Value)), CancellationToken.None);

        Assert.Equal(AgentAuthoringModes.CliCopilot, validation.AuthoringMode);
        Assert.Equal((AgentSyncDiagnosticCodes.CliAgentDeclaresCollection, References), (Assert.Single(validation.Diagnostics).Code, validation.Diagnostics[0].Path));
    }

    private static (string Code, string? Path)[] Findings(AgentWorkspaceValidation validation) => validation.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Path)).ToArray();

    private static AgentWorkspaceSnapshot Scaffold(string authoringMode) => Scaffolds.Agent("Agent", "Contoso Helpdesk", SchemaName, authoringMode, instructions: "Be helpful.");
}
