// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class MetadataConflictMarkerTests
{
    private const string Ours = "local description";
    private const string Theirs = "remote description";

    [Fact]
    public void Build_PlacesBoundariesAtColumnZeroSoVsCodeCanResolveThem()
    {
        var lines = McsConflictMarkers.Build(Ours, Theirs).Split('\n');

        Assert.Equal(5, lines.Length);
        Assert.StartsWith("<<<<<<<", lines[0], StringComparison.Ordinal);
        Assert.Equal(Ours, lines[1]);
        Assert.Equal("=======", lines[2]);
        Assert.Equal(Theirs, lines[3]);
        Assert.StartsWith(">>>>>>>", lines[4], StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ProducesSplitterThatMatchesVsCodeExactComparison()
    {
        var lines = McsConflictMarkers.Build(Ours, Theirs).Split('\n');

        Assert.Contains(lines, line => line == "=======");
    }

    [Fact]
    public void Build_IsDetectedAsAConflict()
    {
        Assert.True(McsConflictMarkers.Contains(McsConflictMarkers.Build(Ours, Theirs)));
    }

    [Theory]
    [InlineData("<<<<<<< ")]
    [InlineData("=======")]
    [InlineData(">>>>>>> ")]
    public void IsMarkerLine_MatchesEveryBoundaryAndTheSplitter(string line)
    {
        Assert.True(McsConflictMarkers.IsMarkerLine(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  description: ordinary")]
    [InlineData("    =======")]
    public void IsMarkerLine_IgnoresIndentedOrOrdinaryLines(string? line)
    {
        Assert.False(McsConflictMarkers.IsMarkerLine(line));
    }

    [Fact]
    public void FindFirstMarkerLine_LocatesTheBoundaryInsideAMetadataBlock()
    {
        var yaml = "mcs.metadata:\n  componentName: skill-1\n" + McsConflictMarkers.Build("  description: a", "  description: b");

        Assert.Equal(3, McsConflictMarkers.FindFirstMarkerLine(yaml));
    }
}
