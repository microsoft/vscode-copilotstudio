// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlTagTests
{
    [Theory]
    [InlineData("category: !!str 010\n", 10)]
    [InlineData("category: !!str 08\n", 8)]
    [InlineData("category: !!str 0x10\n", 16)]
    [InlineData("category: !!str 10\n", 10)]
    [InlineData("category: !!str +10\n", 10)]
    [InlineData("category: !!str -10\n", -10)]
    public void StringTaggedScalarsBindToNumbersWithoutYamlIntegerResolution(string yaml, int expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Category);
    }

    [Theory]
    [InlineData("category: 010\n", 8)]
    [InlineData("category: 0b101\n", 5)]
    [InlineData("category: 1_0\n", 10)]
    [InlineData("category: 0x10\n", 16)]
    public void UntaggedScalarsStillBindUsingYamlIntegerResolution(string yaml, int expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Category);
    }

    [Theory]
    [InlineData("category: !!str 1_0\n")]
    [InlineData("category: !!str 1.9\n")]
    [InlineData("category: !!str 0o10\n")]
    public void StringTaggedScalarsThatAreNotPlainNumbersAreRejected(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Theory]
    [InlineData("subprocess: !!str true\n", true)]
    [InlineData("subprocess: !!str false\n", false)]
    [InlineData("subprocess: !!str 1\n", true)]
    [InlineData("subprocess: !!str 0\n", false)]
    public void StringTaggedScalarsBindToBooleansWithoutYamlBooleanWords(string yaml, bool expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Subprocess);
    }

    [Theory]
    [InlineData("subprocess: !!str yes\n")]
    [InlineData("subprocess: !!str on\n")]
    public void StringTaggedYamlBooleanWordsAreRejected(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml));
    }

    [Theory]
    [InlineData("subprocess: yes\n", true)]
    [InlineData("subprocess: on\n", true)]
    [InlineData("subprocess: no\n", false)]
    public void UntaggedYamlBooleanWordsStillBind(string yaml, bool expected)
    {
        Assert.Equal(expected, McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(yaml)!.Subprocess);
    }

    [Theory]
    [InlineData("a: !!str null\n")]
    [InlineData("a: !!str ~\n")]
    [InlineData("a: !!int null\n")]
    [InlineData("a: !!int ~\n")]
    [InlineData("a: !!bool null\n")]
    [InlineData("a: !!float null\n")]
    public void PlainNullLiteralsResolveToNullEvenWhenTagged(string yaml)
    {
        Assert.Null(McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: [!!float null]\n")]
    [InlineData("a: [!!str ~]\n")]
    [InlineData("a: [!!int null]\n")]
    public void TaggedNullLiteralsResolveToNullInsideFlowSequences(string yaml)
    {
        Assert.Null(Assert.IsType<List<object?>>(McsYamlReader.Parse(yaml)["a"]).Single());
    }

    [Fact]
    public void TaggedNullPropertiesBindAsNullInsteadOfFailing()
    {
        var metadata = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>("description: !!str null\nstateCode: !!int null\nsubprocess: !!bool null\n")!;

        Assert.Null(metadata.Description);
        Assert.Null(metadata.StateCode);
        Assert.Null(metadata.Subprocess);
    }

    [Fact]
    public void QuotedNullLiteralsStayTextEvenWhenTagged()
    {
        Assert.Equal("null", McsYamlReader.Parse("a: !!str 'null'\n")["a"]);
    }

    [Fact]
    public void UntaggedNullKeysStayText()
    {
        Assert.True(Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("a: {null: 1}\n")["a"]).ContainsKey("null"));
    }

    [Theory]
    [InlineData("connectionReferences: [shared_a\n, shared_b]\n")]
    [InlineData("connectionReferences: [shared_a\n  , shared_b]\n")]
    public void FlowSequencesMayPlaceASeparatorOnTheNextLine(string yaml)
    {
        Assert.Equal(new object?[] { "shared_a", "shared_b" }, McsYamlReader.Parse(yaml)["connectionReferences"]);
    }

    [Fact]
    public void FlowMappingsMayPlaceASeparatorOnTheNextLine()
    {
        var map = Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("a: {x: 1\n, y: 2}\n")["a"]);

        Assert.Equal("1", map["x"]);
        Assert.Equal("2", map["y"]);
    }

    [Fact]
    public void FlowSequencesMayPlaceATrailingSeparatorOnTheNextLine()
    {
        Assert.Equal("one", Assert.IsType<List<object?>>(McsYamlReader.Parse("a: [one\n,]\n")["a"]).Single());
    }

    [Theory]
    [InlineData("connectionReferences: [shared_a,# note\n  shared_b]\n")]
    [InlineData("a: [x,#c\ny]\n")]
    [InlineData("a: [#c\n,b]\n")]
    public void AHashThatWouldStartAPlainScalarIsRejected(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("a: [b#notcomment]\n", "b#notcomment")]
    [InlineData("a: [b,c#d]\n", "c#d")]
    public void AHashInsideAPlainScalarIsStillContent(string yaml, string expected)
    {
        Assert.Equal(expected, Assert.IsType<List<object?>>(McsYamlReader.Parse(yaml)["a"]).Last());
    }

    [Theory]
    [InlineData("a: [shared_a, # note\n  shared_b]\n")]
    [InlineData("a: [ #note\n  shared_a, shared_b]\n")]
    public void ProperlySeparatedFlowCommentsAreStillIgnored(string yaml)
    {
        Assert.Contains("shared_b", Assert.IsType<List<object?>>(McsYamlReader.Parse(yaml)["a"]).Select(item => item?.ToString()));
    }

    [Theory]
    [InlineData("a: !<tag:yaml.org,2002:str> hello\n", "hello")]
    [InlineData("a: !<tag:yaml.org,2002:int> 010\n", "8")]
    [InlineData("a: !<tag:yaml.org,2002:bool> yes\n", "True")]
    [InlineData("a: !<tag:yaml.org,2002:float> 1.5\n", "1.5")]
    public void VerbatimStandardTagsResolveLikeTheirShorthand(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]!.ToString());
    }

    [Fact]
    public void VerbatimTagsAreSupportedInsideFlowSequences()
    {
        Assert.Equal("8", Assert.IsType<List<object?>>(McsYamlReader.Parse("a: [!<tag:yaml.org,2002:int> 010]\n")["a"]).Single()!.ToString());
    }

    [Theory]
    [InlineData("%TAG !e! tag:yaml.org,2002:\n---\na: !e!str hello\n", "hello")]
    [InlineData("%TAG !e! tag:yaml.org,2002:\n---\na: !e!int 010\n", "8")]
    [InlineData("%TAG ! tag:yaml.org,2002:\n---\na: !str hello\n", "hello")]
    public void DeclaredTagHandlesResolveToStandardTags(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]!.ToString());
    }

    [Theory]
    [InlineData("a: !foo hello\n")]
    [InlineData("a: !<!foo> hello\n")]
    [InlineData("a: !<tag:example.com,2020:thing> hello\n")]
    [InlineData("a: !e!str hello\n")]
    [InlineData("%TAG !! !my-\n---\na: !!str hello\n")]
    [InlineData("%TAG !m! !my-\n---\na: !m!thing hello\n")]
    [InlineData("a: !<tag:yaml.org,2002:str>hello\n")]
    [InlineData("a: !<tag:yaml.org,2002:str hello\n")]
    [InlineData("a: ! hello\n")]
    public void UnresolvableTagsAreRejected(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void RedeclaringTheSecondaryHandleAppliesToLaterTags()
    {
        Assert.Equal("hello", McsYamlReader.Parse("%TAG !! tag:yaml.org,2002:\n---\na: !!str hello\n")["a"]);
    }
    [Theory]
    [InlineData("!!int 5: value\n")]
    [InlineData("!!bool true: value\n")]
    [InlineData("!!float 1.5: value\n")]
    [InlineData("a: 1\n!!int 5: value\n")]
    [InlineData("outer:\n  !!int 5: value\n")]
    [InlineData("a: {!!int 5: value}\n")]
    public void KeysCarryingANonTextTagAreRejected(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("!!str 5: value\n", "5")]
    [InlineData("a: 1\n!!str b: value\n", "b")]
    [InlineData("outer:\n  !!str key: value\n", "key")]
    public void KeysTaggedAsTextAreAccepted(string yaml, string expectedKey)
    {
        Assert.Contains(expectedKey, Flatten(McsYamlReader.Parse(yaml)));
    }

    [Theory]
    [InlineData("a: !!map\n  k: v\n")]
    [InlineData("a: !!map {k: v}\n")]
    [InlineData("!!map\nk: v\n")]
    [InlineData("a: !!int 5\n")]
    [InlineData("a: !!str text\n")]
    public void TagsOnValuesAndCollectionsAreNotTreatedAsKeyTags(string yaml)
    {
        Assert.NotNull(McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void AMergeKeyIsReadAsAPlainTextKeyJustLikeTheReferenceParser()
    {
        var document = Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("base: &b\n  k: v\nderived:\n  <<: *b\n  own: 1\n")["derived"]);

        Assert.True(document.ContainsKey("<<"));
        Assert.Equal("1", document["own"]);
    }

    private static IEnumerable<string> Flatten(Dictionary<string, object?> document)
    {
        foreach (var entry in document)
        {
            yield return entry.Key;

            if (entry.Value is Dictionary<string, object?> nested)
            {
                foreach (var name in Flatten(nested))
                {
                    yield return name;
                }
            }
        }
    }
}