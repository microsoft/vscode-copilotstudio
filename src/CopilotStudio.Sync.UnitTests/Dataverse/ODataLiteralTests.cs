// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests;

public class ODataLiteralTests
{
    [Theory]
    [InlineData("O'Brien", "'O''Brien'")]
    [InlineData("50%_[x]", "'50%_[x]'")]
    public void Quote_DoublesEveryQuoteAndLeavesWildcardsForAnExactComparison(string value, string expected) =>
        Assert.Equal(expected, ODataLiteral.Quote(value));

    [Theory]
    [InlineData("50%", "'50[%]'")]
    [InlineData("my_flow", "'my[_]flow'")]
    [InlineData("[draft]", "'[[]draft]'")]
    [InlineData("O'Brien", "'O''Brien'")]
    [InlineData("[50%_O'Brien]", "'[[]50[%][_]O''Brien]'")]
    [InlineData("Café_Überblick", "'Café[_]Überblick'")]
    public void LikePattern_MatchesWildcardCharactersLiterally(string value, string expected) =>
        Assert.Equal(expected, ODataLiteral.LikePattern(value));
}
