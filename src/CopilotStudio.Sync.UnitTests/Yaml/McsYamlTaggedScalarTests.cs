// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlTaggedScalarTests
{
    [Fact]
    public void ScalarsWithTheSameTagAndTextAreEqual()
    {
        var left = Tagged("a: !!int 5\n");
        var right = Tagged("b: !!int 5\n");

        Assert.True(left.Equals(right));
        Assert.True(left.Equals((object)right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Theory]
    [InlineData("a: !!int 5\n", "a: !!int 6\n")]
    [InlineData("a: !!int 5\n", "a: !!float 5\n")]
    [InlineData("a: !!int 0x10\n", "a: !!int 16\n")]
    public void ScalarsThatDifferInTagOrTextAreNotEqual(string left, string right)
    {
        Assert.False(Tagged(left).Equals(Tagged(right)));
    }

    [Fact]
    public void ScalarsAreNotEqualToNullOrOtherTypes()
    {
        var tagged = Tagged("a: !!int 5\n");

        Assert.False(tagged.Equals(null));
        Assert.False(tagged.Equals((object?)null));
        Assert.False(tagged.Equals("5"));
    }

    [Theory]
    [InlineData("a: !!int 5\n", "5")]
    [InlineData("a: !!int 0x10\n", "16")]
    [InlineData("a: !!bool yes\n", "True")]
    [InlineData("a: !!float 1.5\n", "1.5")]
    [InlineData("a: !!float .inf\n", "Infinity")]
    [InlineData("a: !!float .nan\n", "NaN")]
    public void ScalarsRenderTheirResolvedValueUsingInvariantFormatting(string yaml, string expected)
    {
        Assert.Equal(expected, Tagged(yaml).ToString());
    }

    [Fact]
    public void ComparerTreatsTaggedScalarsByTagAndText()
    {
        Assert.True(McsYamlComparer.DocumentsMatch("a: !!int 5\n", "a: !!int 5\r\n"));
        Assert.False(McsYamlComparer.DocumentsMatch("a: !!int 5\n", "a: !!int 6\n"));
        Assert.False(McsYamlComparer.DocumentsMatch("a: !!int 0x10\n", "a: !!int 16\n"));
    }

    [Fact]
    public void ComparerTreatsAnUntaggedScalarAsDifferentFromATaggedOne()
    {
        Assert.False(McsYamlComparer.DocumentsMatch("a: 5\n", "a: !!int 5\n"));
    }

    [Theory]
    [InlineData("a: !!float .inf\n", double.PositiveInfinity)]
    [InlineData("a: !!float +.inf\n", double.PositiveInfinity)]
    [InlineData("a: !!float .INF\n", double.PositiveInfinity)]
    [InlineData("a: !!float -.inf\n", double.NegativeInfinity)]
    [InlineData("a: !!float -.INF\n", double.NegativeInfinity)]
    public void InfinityFloatsResolveToTheMatchingDoubleValue(string yaml, double expected)
    {
        Assert.Equal(expected, Assert.IsType<double>(Tagged(yaml).Value));
    }

    [Theory]
    [InlineData("a: !!float .nan\n")]
    [InlineData("a: !!float .NaN\n")]
    [InlineData("a: !!float .NAN\n")]
    public void NotANumberFloatsResolveToDoubleNaN(string yaml)
    {
        Assert.True(double.IsNaN(Assert.IsType<double>(Tagged(yaml).Value)));
    }

    [Theory]
    [InlineData("a: .inf\n", ".inf")]
    [InlineData("a: .nan\n", ".nan")]
    public void UntaggedFloatSpellingsStayText(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: 0o10\n", "0o10")]
    [InlineData("a: 0O10\n", "0O10")]
    public void UnderscoreOctalPrefixIsNotRecognizedAndStaysText(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("category: 0o10\n")]
    [InlineData("category: !!int 0o10\n")]
    public void UnderscoreOctalPrefixIsRejectedWhenBoundToANumber(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Theory]
    [InlineData("category: 0b101\n", 5)]
    [InlineData("category: 0x10\n", 16)]
    [InlineData("category: 010\n", 8)]
    public void OtherRadixPrefixesStillBind(string yaml, int expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Category);
    }

    private static McsYamlTaggedScalar Tagged(string yaml)
        => Assert.IsType<McsYamlTaggedScalar>(McsYamlReader.ParseDocument(yaml).Root.ToValue(preserveStringTags: false) is IDictionary<string, object?> map
            ? map.Values.Single()
            : null);
}
