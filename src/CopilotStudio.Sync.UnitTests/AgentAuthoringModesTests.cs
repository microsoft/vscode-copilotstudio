// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public sealed class AgentAuthoringModesTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("cli-copilot", false)]
    [InlineData("classic", true)]
    [InlineData(" Classic ", true)]
    public void TryParse_AcceptsTheTwoIdsAndABlankValue(string? authoringMode, bool classic)
    {
        Assert.True(AgentAuthoringModes.TryParse(authoringMode, out var parsed));
        Assert.Equal(classic ? AgentAuthoringType.Classic : AgentAuthoringType.Cli, parsed);
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("CLI")]
    [InlineData("clicopilot")]
    [InlineData("CliCopilot")]
    [InlineData("Bogus")]
    [InlineData("class")]
    [InlineData("cli copilot")]
    public void TryParse_RefusesMsagentsSpellingsAndEverythingElse(string authoringMode)
    {
        Assert.False(AgentAuthoringModes.TryParse(authoringMode, out var parsed));
        Assert.Equal(AgentAuthoringType.Cli, parsed);
    }
}
