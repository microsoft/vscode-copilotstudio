// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkspaceFolderNamesTests
{
    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("con.txt", "con_.txt")]
    [InlineData("Lpt9", "Lpt9_")]
    [InlineData("COM10", "COM10")]
    [InlineData("Console", "Console")]
    public void ForWorkflow_GuardsReservedDeviceNames(string name, string expected) =>
        Assert.Equal(expected, WorkspaceFolderNames.ForWorkflow(name));

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("aux.config", "aux_.config")]
    [InlineData("trailing. ", "trailing")]
    [InlineData("a:b*c?d", "a-b-c-d")]
    [InlineData("Tab\there", "Tab-here")]
    [InlineData("  My Agent  ", "My Agent")]
    [InlineData("???", "agent")]
    public void ForProject_ReplacesWhatAFolderCannotHoldIdenticallyOnEveryPlatform(string displayName, string expected) =>
        Assert.Equal(expected, WorkspaceFolderNames.ForProject(displayName));

    [Theory]
    [InlineData(256)]
    [InlineData(300)]
    public void ForProject_CapsALongNameAtTheLongestFolderNameNtfsHolds(int length)
    {
        var displayName = string.Concat(Enumerable.Range(0, length).Select(index => (char)('a' + (index % 26))));

        Assert.Equal(displayName.Substring(0, 255), WorkspaceFolderNames.ForProject(displayName));
    }

    [Theory]
    [InlineData('.')]
    [InlineData(' ')]
    public void ForProject_TrimsTheDotsOrSpacesACappedNameEndsIn(char separator) =>
        Assert.Equal(new string('a', 253), WorkspaceFolderNames.ForProject($"{new string('a', 253)}{separator}{separator}tail"));

    [Fact]
    public void ForProject_LeavesANameThatFitsUnchanged()
    {
        var displayName = string.Concat(Enumerable.Range(0, 255).Select(index => (char)('a' + (index % 26))));

        Assert.Equal(displayName, WorkspaceFolderNames.ForProject(displayName));
    }

    [Fact]
    public void ForProject_NeverSplitsACharacterAtTheCap() =>
        Assert.Equal(new string('a', 254), WorkspaceFolderNames.ForProject($"{new string('a', 254)}\U0001F600tail"));

    [Fact]
    public void ForProject_KeepsAGuardedDeviceNameWithinTheCap() =>
        Assert.Equal($"CON_.{new string('a', 250)}", WorkspaceFolderNames.ForProject($"CON.{new string('a', 251)}"));

    [Fact]
    public void ForWorkflow_CapsALongName() =>
        Assert.Equal(new string('w', 255), WorkspaceFolderNames.ForWorkflow(new string('w', 300)));

    [Fact]
    public void ForWorkflow_CapsALongNameAndKeepsTheIdentifierItEndsWith()
    {
        var workflowId = Guid.NewGuid();

        var folderName = WorkspaceFolderNames.ForWorkflow(new string('w', 300), workflowId);

        Assert.Equal($"{new string('w', 218)}-{workflowId}", folderName);
        Assert.Equal(workflowId, WorkflowWorkspace.WorkflowIdOf(folderName));
    }
}
