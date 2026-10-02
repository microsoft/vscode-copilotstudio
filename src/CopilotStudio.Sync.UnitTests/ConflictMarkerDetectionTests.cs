// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ConflictMarkerDetectionTests
{
    [Theory]
    [InlineData("a: 1\n<<<<<<< ours\nb: 2\n=======\nb: 3\n>>>>>>> theirs\n")]
    [InlineData("<<<<<<<\n")]
    [InlineData(">>>>>>>\n")]
    [InlineData("a: 1\n>>>>>>> theirs\n")]
    public void DetectsConflictMarkers(string text)
    {
        Assert.True(McsConflictMarkers.Contains(text));
    }

    [Theory]
    [InlineData("a: 1\nb: 2\n")]
    [InlineData("a: '<<<< not a marker'\n")]
    [InlineData("description: \"show <<<<<<< in the example\"\n")]
    [InlineData("description: 'show >>>>>>> in the example'\n")]
    [InlineData("description: show <<<<<<< and >>>>>>> in the example\n")]
    [InlineData("description: |\n  show <<<<<<< and >>>>>>> in the example\n")]
    [InlineData("# <<<<<<< ours\n# >>>>>>> theirs\n")]
    [InlineData("description: \"example\\n<<<<<<< ours\\n>>>>>>> theirs\"\n")]
    [InlineData("prefix<<<<<<< ours\r\nprefix>>>>>>> theirs\r\n")]
    [InlineData("=======\n")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresTextWithoutLineLeadingMarkers(string? text)
    {
        Assert.False(McsConflictMarkers.Contains(text));
        Assert.Equal(0, McsConflictMarkers.FindFirstMarkerLine(text));
    }

    [Theory]
    [InlineData("<<<<<<< ours\n", 1)]
    [InlineData("a: 1\n<<<<<<< ours\n", 2)]
    [InlineData("a: 1\nb: 2\nc: 3\n>>>>>>> theirs\n", 4)]
    [InlineData("a: 1\n  <<<<<<< indented\n", 2)]
    [InlineData("content: |\r\n  ---\r\n  \t<<<<<<< ours\r\n  local\r\n  =======\r\n  remote\r\n  >>>>>>> theirs\r\n", 3)]
    [InlineData("content: |\n\t>>>>>>> theirs", 2)]
    [InlineData("  <<<<<<<", 1)]
    [InlineData("description: \"show <<<<<<< in the example\"\n\n  >>>>>>> theirs", 3)]
    public void ReportsFirstMarkerLine(string text, int expectedLine)
    {
        Assert.True(McsConflictMarkers.Contains(text));
        Assert.Equal(expectedLine, McsConflictMarkers.FindFirstMarkerLine(text));
    }

    [Fact]
    public void ContainsMatchesTheSynchronizerGuard()
    {
        const string conflicted = "a: 1\n<<<<<<< ours\nb: 2\n=======\nb: 3\n>>>>>>> theirs\n";

        Assert.Equal(WorkspaceSynchronizer.ContainsConflictMarkers(conflicted), McsConflictMarkers.Contains(conflicted));
    }
}
