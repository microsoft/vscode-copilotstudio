// Copyright (C) Microsoft Corporation. All rights reserved.

using System.Text;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync.Hosting;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class AgentSnapshotLayoutTests
{
    public static TheoryData<string> DeviceNames => new() { "CON", "con", "Prn", "AUX", "NUL", "COM0", "com1", "COM9", "LPT0", "lpt9", "COM\u00B9", "LPT\u00B2", "com\u00B3", "NUL.yml", "con.tar.gz", "AUX ", "CON  .txt", "LPT1." };

    public static TheoryData<string> LookAlikes => new() { "CONSOLE", "COM10", "LPT", "COM", "NULL", "AUXILIARY", "my CON", "CON-tools", "PRN_backup", "COM\u2074", "Contoso HR" };

    public static TheoryData<string> UnsafeFolders => new() { " Contoso", "Contoso ", "Con:toso", "Cont*so", "Contoso?", "\"Contoso\"", "<Contoso>", "Con|toso", "Con\ttoso", "Con\u0001toso" };

    public static TheoryData<string, string> UnsafePaths => new()
    {
        { "Agent/topics/Greeting./x.mcs.yml", "Greeting." },
        { "Agent/topics/Greeting.mcs.yml.", "Greeting.mcs.yml." },
        { "Agent/ /x.mcs.yml", " " },
        { "Agent/to:pics/x.mcs.yml", "to:pics" },
        { "Agent/topics/x\u0007.mcs.yml", "x\u0007.mcs.yml" },
    };

    [Theory]
    [MemberData(nameof(DeviceNames))]
    public void FolderProblem_OfAWindowsDeviceName_ReportsIt(string folder) => Assert.Equal((AgentSyncDiagnosticCodes.ReservedDeviceName, folder), Problem(AgentSnapshotLayout.FolderProblem(folder)));

    [Theory]
    [MemberData(nameof(DeviceNames))]
    public void PathProblem_OfAPathWithADeviceNameInAnySegment_ReportsTheSegment(string segment)
    {
        foreach (var path in new[] { $"{segment}/settings.mcs.yml", $"Agent/{segment}/x.mcs.yml", $"Agent/topics/{segment}" })
        {
            var problem = AgentSnapshotLayout.PathProblem(path);

            Assert.Equal((AgentSyncDiagnosticCodes.ReservedDeviceName, path), Problem(problem));
            Assert.Contains($"'{segment}'", problem!.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(LookAlikes))]
    public void AFolderOrSegmentThatOnlyResemblesADeviceName_IsAccepted(string name)
    {
        Assert.Null(AgentSnapshotLayout.FolderProblem(name));
        Assert.Null(AgentSnapshotLayout.PathProblem($"Agent/{name}/x.mcs.yml"));
    }

    [Theory]
    [MemberData(nameof(UnsafeFolders))]
    public void FolderProblem_OfANameAWindowsPathCannotHold_ReportsItAsUnsafe(string folder) => Assert.Equal((AgentSyncDiagnosticCodes.UnsafePath, folder), Problem(AgentSnapshotLayout.FolderProblem(folder)));

    [Fact]
    public void FolderProblem_OfNoName_ReportsItAsEmpty() => Assert.Equal((AgentSyncDiagnosticCodes.FolderInvalid, "The folder '' is empty."), (AgentSnapshotLayout.FolderProblem(null)!.Code, AgentSnapshotLayout.FolderProblem(null)!.Message));

    [Fact]
    public void AFolderEndingWithADot_IsRefusedAsAFolderNameAndAsAPathSegment()
    {
        Assert.Equal((AgentSyncDiagnosticCodes.UnsafePath, "The folder 'Contoso.' ends with a '.', which a Windows path cannot hold as written."), (AgentSnapshotLayout.FolderProblem("Contoso.")!.Code, AgentSnapshotLayout.FolderProblem("Contoso.")!.Message));
        Assert.Equal(AgentSyncDiagnosticCodes.UnsafePath, AgentSnapshotLayout.PathProblem("Contoso./settings.mcs.yml")!.Code);
    }

    [Theory]
    [InlineData(".x")]
    [InlineData(".git")]
    [InlineData(".config")]
    [InlineData("...")]
    public void FolderProblem_OfANameStartingWithADot_ReportsItAsHidden(string folder) => Assert.Equal((AgentSyncDiagnosticCodes.FolderInvalid, $"The folder '{folder}' starts with a '.', which marks a hidden folder."), (AgentSnapshotLayout.FolderProblem(folder)!.Code, AgentSnapshotLayout.FolderProblem(folder)!.Message));

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".mcs")]
    [InlineData(".MCS")]
    public void FolderProblem_OfAReservedName_KeepsReportingItAsReserved(string folder) => Assert.Equal((AgentSyncDiagnosticCodes.FolderInvalid, $"The folder '{folder}' is a reserved name."), (AgentSnapshotLayout.FolderProblem(folder)!.Code, AgentSnapshotLayout.FolderProblem(folder)!.Message));

    [Fact]
    public void FolderProblem_AcceptsOneHundredCharactersAndRefusesMore()
    {
        var longest = new string('a', AgentSnapshotLayout.MaximumFolderLength);
        var tooLong = longest + "b";

        Assert.Null(AgentSnapshotLayout.FolderProblem(longest));
        Assert.Equal((AgentSyncDiagnosticCodes.FolderInvalid, $"The folder '{tooLong}' holds 101 characters, but a folder name can hold at most 100."), (AgentSnapshotLayout.FolderProblem(tooLong)!.Code, AgentSnapshotLayout.FolderProblem(tooLong)!.Message));
    }

    [Theory]
    [InlineData("../abc./")]
    [InlineData("../.x/")]
    public void DeclaredCollectionFolders_RefusesAFolderTheFolderRuleRefuses(string directory)
    {
        var declarations = AgentSnapshotLayout.DeclaredCollectionFolders("Agent", References(directory));

        Assert.Empty(declarations.Folders);
        Assert.Equal((AgentSyncDiagnosticCodes.CollectionDeclarationInvalid, "Agent/references.mcs.yml"), Problem(Assert.Single(declarations.Problems)));
    }

    [Theory]
    [MemberData(nameof(UnsafePaths))]
    public void PathProblem_OfASegmentAWindowsPathCannotHold_ReportsTheSegment(string path, string segment)
    {
        var problem = AgentSnapshotLayout.PathProblem(path);

        Assert.Equal((AgentSyncDiagnosticCodes.UnsafePath, path), Problem(problem));
        Assert.Contains($"'{segment}'", problem!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PathProblem_ChecksTheTopLevelFolderAgainstTheFoldersGivenOrdinally()
    {
        var folders = new[] { "Agent", "Shared Tools" };

        Assert.Null(AgentSnapshotLayout.PathProblem("Agent/topics/x.mcs.yml", folders));
        Assert.Null(AgentSnapshotLayout.PathProblem("Shared Tools/collection.mcs.yml", folders));
        Assert.Equal((AgentSyncDiagnosticCodes.OutsideDeclaredFolders, "agent/topics/x.mcs.yml"), Problem(AgentSnapshotLayout.PathProblem("agent/topics/x.mcs.yml", folders)));
        Assert.Equal((AgentSyncDiagnosticCodes.OutsideDeclaredFolders, "Other/x.mcs.yml"), Problem(AgentSnapshotLayout.PathProblem("Other/x.mcs.yml", folders)));
        Assert.Null(AgentSnapshotLayout.PathProblem("Other/x.mcs.yml"));
    }

    [Fact]
    public void FolderSetProblems_ReportsEveryInvalidNameAndThenEachFolderNamedTwiceIgnoringCaseAsFirstWritten()
    {
        var problems = AgentSnapshotLayout.FolderSetProblems("Agent", new[] { "Shared Tools", "agent", "NUL", "shared tools", " Padded", "SHARED TOOLS", "Zeta" });

        Assert.Equal(new (string, string?)[] { (AgentSyncDiagnosticCodes.ReservedDeviceName, "NUL"), (AgentSyncDiagnosticCodes.UnsafePath, " Padded"), (AgentSyncDiagnosticCodes.DuplicateCollection, "Agent"), (AgentSyncDiagnosticCodes.DuplicateCollection, "Shared Tools") }, problems.Select(Problem));
        Assert.Equal("The folder 'Shared Tools' is named more than once, ignoring case.", problems[3].Message);
    }

    [Fact]
    public void FolderSetProblems_OfDistinctValidFolders_ReportsNothing() => Assert.Empty(AgentSnapshotLayout.FolderSetProblems("Agent", new[] { "Shared Tools", "Zeta" }));

    [Fact]
    public void FolderSetProblems_OfAnInvalidAgentFolder_ReportsIt() => Assert.Equal((AgentSyncDiagnosticCodes.ReservedDeviceName, "COM1"), Problem(Assert.Single(AgentSnapshotLayout.FolderSetProblems("COM1", Array.Empty<string>()))));

    [Fact]
    public void DeclaredCollectionFolders_ReportsAFolderDeclaredAgainInAnotherCaseAndKeepsItOnce()
    {
        var declarations = AgentSnapshotLayout.DeclaredCollectionFolders("Agent", References("../Shared Tools/", "../shared tools/", "../Shared Tools/", "../AGENT/", "../CON/"));

        Assert.Equal(new[] { "Shared Tools" }, declarations.Folders);
        Assert.Equal(new (string, string?)[] { (AgentSyncDiagnosticCodes.CollectionDeclarationInvalid, "Agent/references.mcs.yml"), (AgentSyncDiagnosticCodes.DuplicateCollection, "Agent/references.mcs.yml"), (AgentSyncDiagnosticCodes.DuplicateCollection, "Agent/references.mcs.yml") }, declarations.Problems.Select(Problem));
        Assert.Equal(new[] { "The folder 'Agent' is named more than once, ignoring case.", "The folder 'Shared Tools' is named more than once, ignoring case." }, declarations.Problems.Skip(1).Select(problem => problem.Message));
    }

    [Fact]
    public void DeclaredCollectionFolders_OfAWorkspaceWithoutReferences_DeclaresNothing()
    {
        var declarations = AgentSnapshotLayout.DeclaredCollectionFolders("Agent", NewWorkspace());

        Assert.Empty(declarations.Folders);
        Assert.Empty(declarations.Problems);
    }

    private static (string Code, string? Path) Problem(AgentSyncDiagnostic? diagnostic) => (diagnostic!.Code, diagnostic.Path);

    private static InMemoryFileAccessor NewWorkspace() => new(new DirectoryPath("c:/test/layout/"));

    private static InMemoryFileAccessor References(params string[] directories)
    {
        var workspace = NewWorkspace();
        workspace.Write(WorkspaceSynchronizer.ReferencesCollectionPath, Encoding.UTF8.GetBytes("componentCollections:\n" + string.Concat(directories.Select((directory, index) => $"  - schemaName: cr_tools{index}\n    directory: {directory}\n"))));

        return workspace;
    }
}
