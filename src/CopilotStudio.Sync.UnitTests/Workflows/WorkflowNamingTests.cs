// Copyright (C) Microsoft Corporation. All rights reserved.

using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class WorkflowNamingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Validate_AcceptsOneHundredCharactersHoweverMuchSpaceSurroundsThem(int padding) =>
        Assert.Null(WorkflowNaming.Validate(new string(' ', padding) + new string('a', 100) + new string(' ', padding)));

    [Fact]
    public void Validate_RefusesANameLongerThanOneHundredCharacters() =>
        Assert.NotNull(WorkflowNaming.Validate(new string('a', 101)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line\nbreak")]
    [InlineData("Tab\there")]
    public void Validate_RefusesAMissingNameOrOneHoldingAControlCharacter(string? name) =>
        Assert.NotNull(WorkflowNaming.Validate(name));
}
