// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlComparerTests
{
    [Fact]
    public void MatchesIdenticalText()
    {
        Assert.True(McsYamlComparer.DocumentsMatch("name: Flow\r\nstateCode: 0\r\n", "name: Flow\r\nstateCode: 0\r\n"));
    }

    [Fact]
    public void MatchesMultiLineDescriptionAcrossSerializerFormats()
    {
        Assert.True(McsYamlComparer.DocumentsMatch(
            "name: Flow\r\ndescription: >-\r\n  first line\r\n\r\n  second line\r\nstateCode: 0\r\n",
            "name: Flow\r\ndescription: \"first line\\nsecond line\"\r\nstateCode: 0\r\n"));
    }

    [Fact]
    public void MatchesDifferentScalarQuotingOfTheSameValue()
    {
        Assert.True(McsYamlComparer.DocumentsMatch("name: 'Flow'\r\n", "name: Flow\r\n"));
    }

    [Fact]
    public void MatchesDifferentSequenceIndentation()
    {
        Assert.True(McsYamlComparer.DocumentsMatch(
            "connectionReferences:\r\n- shared_a\r\n- shared_b\r\n",
            "connectionReferences:\r\n  - shared_a\r\n  - shared_b\r\n"));
    }

    [Fact]
    public void DoesNotMatchWhenAValueChanges()
    {
        Assert.False(McsYamlComparer.DocumentsMatch("name: Flow\r\nstateCode: 0\r\n", "name: Flow\r\nstateCode: 1\r\n"));
    }

    [Fact]
    public void DoesNotMatchWhenAFieldIsAdded()
    {
        Assert.False(McsYamlComparer.DocumentsMatch("name: Flow\r\n", "name: Flow\r\nstateCode: 0\r\n"));
    }

    [Fact]
    public void DoesNotMatchWhenSequenceOrderChanges()
    {
        Assert.False(McsYamlComparer.DocumentsMatch(
            "connectionReferences:\r\n- shared_a\r\n- shared_b\r\n",
            "connectionReferences:\r\n- shared_b\r\n- shared_a\r\n"));
    }

    [Fact]
    public void DoesNotMatchNullAgainstText()
    {
        Assert.False(McsYamlComparer.DocumentsMatch("name: \r\n", "name: 'null'\r\n"));
    }

    [Theory]
    [InlineData("name: Flow\r\n", "")]
    [InlineData("", "name: Flow\r\n")]
    [InlineData("name: 'unterminated\r\n", "name: Flow\r\n")]
    public void DoesNotMatchWhenEitherDocumentIsUnusable(string left, string right)
    {
        Assert.False(McsYamlComparer.DocumentsMatch(left, right));
    }
}
