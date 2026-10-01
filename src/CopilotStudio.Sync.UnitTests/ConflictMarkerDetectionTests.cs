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
    [InlineData("a: 1\n  <<<<<<< indented\n")]
    [InlineData("")]
    [InlineData(null)]
    public void IgnoresTextWithoutLineLeadingMarkers(string? text)
    {
        Assert.Equal(0, McsConflictMarkers.FindFirstMarkerLine(text));
    }

    [Theory]
    [InlineData("<<<<<<< ours\n", 1)]
    [InlineData("a: 1\n<<<<<<< ours\n", 2)]
    [InlineData("a: 1\nb: 2\nc: 3\n>>>>>>> theirs\n", 4)]
    public void ReportsFirstMarkerLine(string text, int expectedLine)
    {
        Assert.Equal(expectedLine, McsConflictMarkers.FindFirstMarkerLine(text));
    }

    [Fact]
    public void ContainsMatchesTheSynchronizerGuard()
    {
        const string conflicted = "a: 1\n<<<<<<< ours\nb: 2\n=======\nb: 3\n>>>>>>> theirs\n";

        Assert.Equal(WorkspaceSynchronizer.ContainsConflictMarkers(conflicted), McsConflictMarkers.Contains(conflicted));
    }
}
