// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlWriterTests
{
    [Fact]
    public void WritesNullAsKeyWithTrailingSpace()
    {
        Assert.Equal(LineEndings.ToPlatform("description: \r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["description"] = null }));
    }

    [Fact]
    public void WritesEmptySequenceInFlowStyle()
    {
        Assert.Equal(LineEndings.ToPlatform("connectionReferences: []\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["connectionReferences"] = new List<object?>() }));
    }

    [Fact]
    public void WritesSequenceItemsAtKeyIndent()
    {
        Assert.Equal(
            LineEndings.ToPlatform("connectionReferences:\r\n- shared_a\r\n- shared_b\r\n"),
            McsYamlWriter.Write(new Dictionary<string, object?> { ["connectionReferences"] = new List<object?> { "shared_a", "shared_b" } }));
    }

    [Fact]
    public void WritesNestedMappingWithTwoSpaceIndent()
    {
        Assert.Equal(
            LineEndings.ToPlatform("isCustomizable:\r\n  value: true\r\n  canBeChanged: false\r\n"),
            McsYamlWriter.Write(new Dictionary<string, object?>
            {
                ["isCustomizable"] = new Dictionary<string, object?> { ["value"] = "true", ["canBeChanged"] = "false" },
            }));
    }

    [Fact]
    public void WritesSequenceOfMappings()
    {
        Assert.Equal(
            LineEndings.ToPlatform("items:\r\n- kind: Http\r\n  method: POST\r\n- kind: Timer\r\n"),
            McsYamlWriter.Write(new Dictionary<string, object?>
            {
                ["items"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["kind"] = "Http", ["method"] = "POST" },
                    new Dictionary<string, object?> { ["kind"] = "Timer" },
                },
            }));
    }

    [Theory]
    [InlineData("plain", "a: plain\r\n")]
    [InlineData("", "a: ''\r\n")]
    [InlineData("  padded  ", "a: '  padded  '\r\n")]
    [InlineData("#hash", "a: '#hash'\r\n")]
    [InlineData("Name: colon", "a: 'Name: colon'\r\n")]
    [InlineData("- dash", "a: '- dash'\r\n")]
    [InlineData("it's", "a: it's\r\n")]
    [InlineData("trailing:", "a: 'trailing:'\r\n")]
    [InlineData("has # hash", "a: 'has # hash'\r\n")]
    [InlineData("1.0", "a: 1.0\r\n")]
    [InlineData("yes", "a: yes\r\n")]
    [InlineData("null", "a: 'null'\r\n")]
    [InlineData("Null", "a: 'Null'\r\n")]
    [InlineData("NULL", "a: 'NULL'\r\n")]
    [InlineData("~", "a: '~'\r\n")]
    public void QuotesScalarsOnlyWhenRequired(string value, string expected)
    {
        Assert.Equal(LineEndings.ToPlatform(expected), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = value }));
    }

    [Theory]
    [InlineData("#hidden", "'#hidden': x\r\n")]
    [InlineData("a: b", "'a: b': x\r\n")]
    [InlineData("plain", "plain: x\r\n")]
    [InlineData("null", "'null': x\r\n")]
    [InlineData("... custom", "'... custom': x\r\n")]
    [InlineData("...", "'...': x\r\n")]
    [InlineData("--- custom", "'--- custom': x\r\n")]
    [InlineData("...notamarker", "...notamarker: x\r\n")]
    public void QuotesKeysWhenRequired(string key, string expected)
    {
        Assert.Equal(LineEndings.ToPlatform(expected), McsYamlWriter.Write(new Dictionary<string, object?> { [key] = "x" }));
    }

    [Fact]
    public void WritesOverlongKeysUsingExplicitKeySyntax()
    {
        var key = new string('a', 1030);

        Assert.Equal(LineEndings.ToPlatform($"? {key}\r\n: x\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { [key] = "x" }));
    }

    [Fact]
    public void ReadsBackOverlongKeys()
    {
        var key = new string('a', 1030);
        var document = new Dictionary<string, object?> { [key] = "x" };

        Assert.Equal(document, McsYamlReader.Parse(McsYamlWriter.Write(document)));
    }

    [Fact]
    public void WritesOverlongFirstKeyOfASequenceMappingOnItsOwnDash()
    {
        var key = new string('a', 1030);

        Assert.Equal(
            LineEndings.ToPlatform($"items:\r\n-\r\n  ? {key}\r\n  : x\r\n"),
            McsYamlWriter.Write(new Dictionary<string, object?> { ["items"] = new List<object?> { new Dictionary<string, object?> { [key] = "x" } } }));
    }

    [Fact]
    public void ReadsBackDocumentMarkerLikeKeys()
    {
        var document = new Dictionary<string, object?> { ["... custom"] = "x", ["--- custom"] = "y" };

        Assert.Equal(document, McsYamlReader.Parse(McsYamlWriter.Write(document)));
    }

    [Theory]
    [InlineData("x\0y", "a: \"x\\0y\"\r\n")]
    [InlineData("x\u2028y", "a: \"x\\Ly\"\r\n")]
    [InlineData("x\u0085y", "a: \"x\\Ny\"\r\n")]
    [InlineData("x\u0007y", "a: \"x\\ay\"\r\n")]
    public void EscapesCharactersThatWouldBreakParsing(string value, string expected)
    {
        Assert.Equal(LineEndings.ToPlatform(expected), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = value }));
    }

    [Fact]
    public void WritesEmptyMappingInFlowStyle()
    {
        Assert.Equal(LineEndings.ToPlatform("a: {}\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = new Dictionary<string, object?>() }));
    }

    [Fact]
    public void WritesEmptyMappingSequenceItem()
    {
        Assert.Equal(LineEndings.ToPlatform("a:\r\n- {}\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = new List<object?> { new Dictionary<string, object?>() } }));
    }

    [Fact]
    public void WritesNestedSequenceItems()
    {
        Assert.Equal(
            LineEndings.ToPlatform("a:\r\n-\r\n  - one\r\n  - two\r\n- []\r\n"),
            McsYamlWriter.Write(new Dictionary<string, object?>
            {
                ["a"] = new List<object?> { new List<object?> { "one", "two" }, new List<object?>() },
            }));
    }

    [Fact]
    public void WritesMultiLineScalarDoubleQuoted()
    {
        Assert.Equal(LineEndings.ToPlatform("a: \"line1\\nline2\"\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = "line1\nline2" }));
    }

    [Fact]
    public void WritesTabScalarQuoted()
    {
        Assert.Equal(LineEndings.ToPlatform("a: \"tab\\there\"\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = "tab\there" }));
    }

    [Fact]
    public void PreservesInsertionOrder()
    {
        Assert.Equal(
            LineEndings.ToPlatform("z: 1\r\na: 2\r\nm: 3\r\n"),
            McsYamlWriter.Write(new Dictionary<string, object?> { ["z"] = "1", ["a"] = "2", ["m"] = "3" }));
    }

    [Fact]
    public void PreservesUnknownFieldsWhenMutatingOneValue()
    {
        const string Original =
            "workflowId: 4f66c140-e032-f111-88b4-7ced8d3b6119\r\n" +
            "name: Agent Flow 1\r\n" +
            "someFutureField: keep me\r\n" +
            "stateCode: 0\r\n" +
            "statusCode: 1\r\n" +
            "nestedFuture:\r\n" +
            "  inner: also keep\r\n" +
            "connectionReferences:\r\n" +
            "- shared_a\r\n";

        var document = McsYamlReader.Parse(Original);
        document["stateCode"] = "1";
        document["statusCode"] = "2";

        Assert.Equal(
            LineEndings.ToPlatform(Original.Replace("stateCode: 0", "stateCode: 1", StringComparison.Ordinal).Replace("statusCode: 1", "statusCode: 2", StringComparison.Ordinal)),
            McsYamlWriter.Write(document));
    }

    [Theory]
    [MemberData(nameof(RoundTripDocuments))]
    public void RoundTripsThroughOwnReader(Dictionary<string, object?> document)
    {
        Assert.Equal(document, McsYamlReader.Parse(McsYamlWriter.Write(document)));
    }

    [Fact]
    public void WritesLazySequencesWithoutReEnumerating()
    {
        var enumerations = 0;

        IEnumerable<object?> Lazy()
        {
            enumerations++;
            yield return "one";
            yield return "two";
        }

        Assert.Equal(LineEndings.ToPlatform("list:\r\n- one\r\n- two\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["list"] = Lazy() }));
        Assert.Equal(1, enumerations);
    }

    [Fact]
    public void WritesEmptyLazySequenceInline()
    {
        IEnumerable<object?> Empty()
        {
            yield break;
        }

        Assert.Equal(LineEndings.ToPlatform("list: []\r\n"), McsYamlWriter.Write(new Dictionary<string, object?> { ["list"] = Empty() }));
    }

    public static TheoryData<Dictionary<string, object?>> RoundTripDocuments() => new()
    {
        new Dictionary<string, object?> { ["name"] = "My Flow", ["type"] = "1" },
        new Dictionary<string, object?> { ["name"] = "x", ["description"] = null },
        new Dictionary<string, object?> { ["list"] = new List<object?>() },
        new Dictionary<string, object?> { ["list"] = new List<object?> { "a", "cre98_AgentC1.cr.WX3p-EQ4" } },
        new Dictionary<string, object?> { ["map"] = new Dictionary<string, object?> { ["value"] = "true" } },
        new Dictionary<string, object?> { ["description"] = "line1\nline2\nline3" },
        new Dictionary<string, object?> { ["name"] = "Name: with colon", ["other"] = "#hash" },
        new Dictionary<string, object?> { ["padded"] = "  pad  ", ["empty"] = "" },
        new Dictionary<string, object?> { ["unicode"] = "é中文" },
        new Dictionary<string, object?> { ["introducedVersion"] = "1.0" },
        new Dictionary<string, object?>
        {
            ["items"] = new List<object?>
            {
                new Dictionary<string, object?> { ["kind"] = "Http", ["method"] = "POST" },
                new Dictionary<string, object?> { ["kind"] = "Timer" },
            },
        },
        new Dictionary<string, object?>
        {
            ["outer"] = new Dictionary<string, object?> { ["inner"] = new Dictionary<string, object?> { ["deepest"] = "value" } },
        },
    };
}
