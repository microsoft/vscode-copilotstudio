// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlReaderTests
{
    [Theory]
    [InlineData("a: hello", "hello")]
    [InlineData("a: 'Name: colon'", "Name: colon")]
    [InlineData("a: '#hash'", "#hash")]
    [InlineData("a: '  padded  '", "  padded  ")]
    [InlineData("a: ''", "")]
    [InlineData("a: \"line1\\nline2\"", "line1\nline2")]
    [InlineData("a: \"tab\\there\"", "tab\there")]
    [InlineData("a: \"quote\\\"inside\"", "quote\"inside")]
    [InlineData("a: \"back\\\\slash\"", "back\\slash")]
    [InlineData("a: \"\\u00e9\\u4e2d\"", "é中")]
    [InlineData("a: \"\\U0001F600\"", "😀")]
    [InlineData("a: \"\\x41\"", "A")]
    [InlineData("a: \"\\e\"", "\u001b")]
    [InlineData("a: \"\\0\"", "\0")]
    [InlineData("a: 'it''s'", "it's")]
    [InlineData("a: don't", "don't")]
    [InlineData("a: don't # comment", "don't")]
    [InlineData("a: x\t# comment", "x")]
    [InlineData("a: http://example.com/x", "http://example.com/x")]
    public void ReadsScalarForms(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("123")]
    [InlineData("1.0")]
    [InlineData("2021-01-01")]
    [InlineData("0x1F")]
    public void DoesNotCoerceScalarLookAlikes(string raw)
    {
        Assert.Equal(raw, McsYamlReader.Parse($"introducedVersion: {raw}")["introducedVersion"]);
    }

    [Theory]
    [InlineData("a: null\n")]
    [InlineData("a: Null\n")]
    [InlineData("a: NULL\n")]
    [InlineData("a: ~\n")]
    [InlineData("a: \nb: x\n")]
    public void ResolvesNullScalars(string yaml)
    {
        Assert.Null(McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: 'null'\n", "null")]
    [InlineData("a: '~'\n", "~")]
    [InlineData("a: \"null\"\n", "null")]
    [InlineData("a: !!str 'null'\n", "null")]
    public void KeepsQuotedNullLiteralsAsText(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void ReadsNestedMapping()
    {
        var value = Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("isCustomizable:\n  value: true\n  canBeChanged: false\n")["isCustomizable"]);
        Assert.Equal("true", value["value"]);
        Assert.Equal("false", value["canBeChanged"]);
    }

    [Fact]
    public void ReadsDeeplyNestedMapping()
    {
        var root = Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("a:\n  b:\n    c:\n      d: deep\n")["a"]);
        var b = Assert.IsType<Dictionary<string, object?>>(root["b"]);
        var c = Assert.IsType<Dictionary<string, object?>>(b["c"]);
        Assert.Equal("deep", c["d"]);
    }

    [Theory]
    [InlineData("a:\n- one\n- two\n")]
    [InlineData("a:\n  - one\n  - two\n")]
    public void ReadsSequenceAtEitherIndent(string yaml)
    {
        Assert.Equal(new object?[] { "one", "two" }, Assert.IsType<List<object?>>(McsYamlReader.Parse(yaml)["a"]));
    }

    [Fact]
    public void ReadsEmptySequenceItemWithoutConsumingSiblings()
    {
        Assert.Equal(new object?[] { null, "two" }, Assert.IsType<List<object?>>(McsYamlReader.Parse("a:\n-\n- two\n")["a"]));
    }

    [Fact]
    public void ReadsNestedSequence()
    {
        var outer = Assert.IsType<List<object?>>(McsYamlReader.Parse("a:\n- - one\n  - two\n")["a"]);
        Assert.Equal(new object?[] { "one", "two" }, Assert.IsType<List<object?>>(Assert.Single(outer)));
    }

    [Fact]
    public void ReadsBlockScalarInsideSequence()
    {
        Assert.Equal(new object?[] { "hello" }, Assert.IsType<List<object?>>(McsYamlReader.Parse("a:\n- |-\n  hello\n")["a"]));
    }

    [Fact]
    public void ReadsSequenceMappingWithExtraSeparationSpaces()
    {
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(McsYamlReader.Parse("a:\n-   key: x\n    other: y\n")["a"])));
        Assert.Equal("x", item["key"]);
        Assert.Equal("y", item["other"]);
    }

    [Fact]
    public void ReadsEmptyFlowSequence()
    {
        Assert.Empty(Assert.IsType<List<object?>>(McsYamlReader.Parse("connectionReferences: []\n")["connectionReferences"]));
    }

    [Theory]
    [InlineData("a: [one, two]\n")]
    [InlineData("a: [one, two,]\n")]
    [InlineData("a: [one,\n  two]\n")]
    public void ReadsFlowSequenceForms(string yaml)
    {
        Assert.Equal(new object?[] { "one", "two" }, Assert.IsType<List<object?>>(McsYamlReader.Parse(yaml)["a"]));
    }

    [Fact]
    public void ReadsFlowSequenceWithQuotedItems()
    {
        Assert.Equal(new object?[] { "x, y", "z" }, Assert.IsType<List<object?>>(McsYamlReader.Parse("a: ['x, y', \"z\"]\n")["a"]));
    }

    [Theory]
    [InlineData("a: {x: 1, y: 2}\n")]
    [InlineData("a: {x: 1, y: 2,}\n")]
    public void ReadsFlowMappingForms(string yaml)
    {
        var value = Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse(yaml)["a"]);
        Assert.Equal("1", value["x"]);
        Assert.Equal("2", value["y"]);
    }

    [Fact]
    public void ReadsFlowMappingOmittedValueAsNull()
    {
        Assert.Null(Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("a: {one: }\n")["a"])["one"]);
    }

    [Fact]
    public void ReadsFlowKeyContainingColon()
    {
        Assert.Equal("value", Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("a: {http://x: value}\n")["a"])["http://x"]);
    }

    [Fact]
    public void ReadsFlowPairInsideSequence()
    {
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(Assert.IsType<List<object?>>(McsYamlReader.Parse("a: [one: 1]\n")["a"])));
        Assert.Equal("1", item["one"]);
    }

    [Fact]
    public void ReadsFlowSetAsNullValuedMapping()
    {
        var value = Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("a: {one, two}\n")["a"]);
        Assert.Null(value["one"]);
        Assert.Null(value["two"]);
    }

    [Fact]
    public void ReadsSequenceOfMappings()
    {
        var items = Assert.IsType<List<object?>>(McsYamlReader.Parse("items:\n- kind: Http\n  method: POST\n- kind: Timer\n  interval: 5\n")["items"]);
        Assert.Equal(2, items.Count);
        Assert.Equal("POST", Assert.IsType<Dictionary<string, object?>>(items[0])["method"]);
        Assert.Equal("5", Assert.IsType<Dictionary<string, object?>>(items[1])["interval"]);
    }

    [Fact]
    public void ReadsMappingContainingSequence()
    {
        var config = Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("config:\n  items:\n  - a\n  - b\n  flag: true\n")["config"]);
        Assert.Equal(new object?[] { "a", "b" }, Assert.IsType<List<object?>>(config["items"]));
        Assert.Equal("true", config["flag"]);
    }

    [Theory]
    [InlineData("a: >-\n  line1\n\n  line2\nb: x\n", "line1\nline2")]
    [InlineData("a: >\n  text\nb: x\n", "text\n")]
    [InlineData("a: |-\n  line1\n  line2\nb: x\n", "line1\nline2")]
    [InlineData("a: |\n  line1\nb: x\n", "line1\n")]
    [InlineData("a: |+\n  hello\n\nb: x\n", "hello\n\n")]
    [InlineData("a: |+\n\nb: x\n", "\n")]
    [InlineData("a: |\n  hello", "hello")]
    [InlineData("a: >-\n  first\n    code\n  last\nb: x\n", "first\n  code\nlast")]
    [InlineData("a: >2-\n    leading\n  next\nb: x\n", "  leading\nnext")]
    public void ReadsBlockScalars(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void FoldsWrappedParagraphsIntoSpaces()
    {
        Assert.Equal("one two three", McsYamlReader.Parse("a: >-\n  one\n  two\n  three\nb: x\n")["a"]);
    }

    [Theory]
    [InlineData("a: hello\n  world\n", "hello world")]
    [InlineData("a: 'hello\n  world'\n", "hello world")]
    [InlineData("a: hello\n\n  world\n", "hello\nworld")]
    public void ReadsMultiLineScalars(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void ResolvesAnchorsAndAliases()
    {
        var result = McsYamlReader.Parse("a: &shared one\nb: *shared\n");
        Assert.Equal("one", result["a"]);
        Assert.Equal("one", result["b"]);
    }

    [Fact]
    public void ResolvesAliasesInsideFlowCollections()
    {
        Assert.Equal(new object?[] { "one", "one" }, Assert.IsType<List<object?>>(McsYamlReader.Parse("a: [&shared one, *shared]\n")["a"]));
    }

    [Fact]
    public void IgnoresTagsOnScalars()
    {
        Assert.Equal("1", McsYamlReader.Parse("a: !!str 1\n")["a"]);
    }

    [Fact]
    public void SkipsCommentsAndDocumentMarkers()
    {
        var result = McsYamlReader.Parse("# leading\n---\na: 1 # trailing\n# middle\nb: 2\n...\n");
        Assert.Equal("1", result["a"]);
        Assert.Equal("2", result["b"]);
    }

    [Fact]
    public void SkipsDirectives()
    {
        Assert.Equal("1", McsYamlReader.Parse("%YAML 1.1\n---\na: 1\n")["a"]);
    }

    [Fact]
    public void StripsByteOrderMark()
    {
        Assert.Equal(new[] { "a", "b" }, McsYamlReader.Parse("\uFEFFa: 1\nb: 2\n").Keys);
    }

    [Theory]
    [InlineData("a: 1\r\nb: 2\r\n")]
    [InlineData("a: 1\rb: 2\r")]
    public void ReadsCarriageReturnLineBreaks(string yaml)
    {
        var result = McsYamlReader.Parse(yaml);
        Assert.Equal("1", result["a"]);
        Assert.Equal("2", result["b"]);
    }

    [Fact]
    public void ReadsEmptyDocumentAsEmptyMapping()
    {
        Assert.Empty(McsYamlReader.Parse(string.Empty));
    }

    [Fact]
    public void KeepsLastValueForDuplicateKeys()
    {
        Assert.Equal("2", McsYamlReader.Parse("a: 1\na: 2\n")["a"]);
    }

    [Theory]
    [InlineData("a:\n  <<: *base\n  b: 1\n")]
    [InlineData("a: *missing\n")]
    [InlineData("a: 'unterminated\n")]
    [InlineData("a: \"unterminated\n")]
    [InlineData("a: [1, 2\n")]
    [InlineData("a: {x: 1\n")]
    [InlineData("a: [one,,two]\n")]
    [InlineData("a: {one: two: three}\n")]
    [InlineData("a: b: c\n")]
    [InlineData("a: ]\n")]
    [InlineData("a: @hi\n")]
    [InlineData("a: %hi\n")]
    [InlineData("a:\n\tb: 1\n")]
    [InlineData("  a: 1\nb: 2\n")]
    [InlineData("a: 1\n---\nb: 2\n")]
    [InlineData("a: 1\n...\nb: 2\n")]
    [InlineData("a: |\n    hello\n  world\n")]
    [InlineData("a: \"\\q\"\n")]
    [InlineData("a: \"\\u12\"\n")]
    [InlineData("a: \"\\uZZZZ\"\n")]
    public void ThrowsOnUnsupportedOrMalformedInput(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("- one\n- two\n")]
    [InlineData("[one, two]\n")]
    [InlineData("hello\n")]
    public void ThrowsWhenTheRootIsNotAMapping(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void ThrowsOnDeeplyNestedDocuments()
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(string.Concat(Enumerable.Repeat("a: [", 500)) + string.Concat(Enumerable.Repeat("]", 500))));
    }

    [Theory]
    [InlineData("a: \"\\uD800\"\n")]
    [InlineData("a: \"\\uDC00\"\n")]
    [InlineData("a: \"\\U00110000\"\n")]
    public void ThrowsOnUnicodeEscapesOutsideTheValidRange(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void ThrowsWhenAliasesExpandBeyondTheNodeBudget()
    {
        var builder = new System.Text.StringBuilder("a0: &n0 [leaf]\n");
        for (var level = 1; level <= 20; level++)
        {
            builder.Append($"a{level}: &n{level} [*n{level - 1}, *n{level - 1}]\n");
        }

        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(builder.ToString()));
    }

    [Fact]
    public void ThrowsWhenAliasesExpandBeyondTheNestingLimit()
    {
        var builder = new System.Text.StringBuilder("a0: &n0 leaf\n");
        for (var level = 1; level <= 28; level++)
        {
            builder.Append($"a{level}: &n{level} {new string('[', 120)}*n{level - 1}{new string(']', 120)}\n");
        }

        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(builder.ToString()));
    }

    [Fact]
    public void ReadsLargeSequencesWithoutAliases()
    {
        var items = McsYamlReader.Parse("name: Flow\ncustom: [" + string.Join(",", Enumerable.Repeat("x", 50000)) + "]\n")["custom"];

        Assert.Equal(50000, Assert.IsType<List<object?>>(items).Count);
    }

    [Theory]
    [InlineData("a: |- # comment\n  text\n", "text")]
    [InlineData("a: |- # comment\n\n  text\n", "\ntext")]
    [InlineData("a: | # comment\n  text\n", "text\n")]
    public void ReadsBlockScalarsWithACommentOnTheHeader(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: |+\n  ", "")]
    [InlineData("a: |+\n\n  ", "\n")]
    [InlineData("a: |+\n\n", "\n")]
    public void KeepsTerminationStateOfTrailingBlankBlockScalarLines(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void FoldedScalarKeepsLeadingBlankLineBeforeIndentedText()
    {
        Assert.Equal("\n  text", McsYamlReader.Parse("a: >2-\n\n    text\n")["a"]);
    }

    [Theory]
    [InlineData("a: \"x\\\n\n  y\"\n", "x\ny")]
    [InlineData("a: \"x\\\n\n\n  y\"\n", "x\n\ny")]
    [InlineData("a: \"one  \\\n  two\"\n", "one  two")]
    [InlineData("a: \"one\\\n  two\"\n", "onetwo")]
    public void EscapedLineBreaksStillCountTowardsFolding(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: 'x\u2028  y'\n", "x\u2028y")]
    [InlineData("a: 'x\u2029  y'\n", "x\u2029y")]
    [InlineData("a: 'x\u0085  y'\n", "x y")]
    public void PreservesParagraphAndLineSeparatorsInsideScalars(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void ReadsMappingSeparatorAfterANextLineBreak()
    {
        Assert.Equal(new Dictionary<string, object?> { ["b"] = "c" }, McsYamlReader.Parse("a:\u0085  b: c\n")["a"]);
    }

    [Fact]
    public void ReadsCommentTerminatedByANextLineBreak()
    {
        Assert.Equal(new Dictionary<string, object?> { ["a"] = "x", ["b"] = "y" }, McsYamlReader.Parse("a: x\u0085# comment\nb: y\n"));
    }

    [Fact]
    public void ResolvesNullLiteralsInFlowSequences()
    {
        Assert.Equal(new List<object?> { null, null, null, null }, McsYamlReader.Parse("a: [null, Null, NULL, ~]\n")["a"]);
    }

    [Fact]
    public void KeepsQuotedNullLikeTextInFlowSequences()
    {
        Assert.Equal(new List<object?> { "null", "~" }, McsYamlReader.Parse("a: ['null', '~']\n")["a"]);
    }

    [Fact]
    public void ReadsExplicitKeyEntryInAFlowSequence()
    {
        Assert.Equal(new List<object?> { new Dictionary<string, object?> { ["one"] = null } }, McsYamlReader.Parse("a: [? one]\n")["a"]);
    }

    [Fact]
    public void ReadsExplicitFlowKeySeparatedByALineBreak()
    {
        Assert.Equal(new Dictionary<string, object?> { ["one"] = "two" }, McsYamlReader.Parse("a: {?\n one: two}\n")["a"]);
    }

    [Fact]
    public void AnchorOnTheFirstKeyBindsToThatKey()
    {
        Assert.Equal("inner", McsYamlReader.Parse("outer:\n  &k inner: value\nother: *k\n")["other"]);
    }

    [Fact]
    public void AnchorOnTheFirstKeyIsAvailableWithinItsOwnValue()
    {
        Assert.Equal(new Dictionary<string, object?> { ["name"] = "name" }, McsYamlReader.Parse("&k name: *k\n"));
    }

    [Fact]
    public void AnchorOnTheFirstKeyIsAvailableWithinItsOwnMapping()
    {
        Assert.Equal(new Dictionary<string, object?> { ["inner"] = "value", ["other"] = "inner" }, McsYamlReader.Parse("outer:\n  &k inner: value\n  other: *k\n")["outer"]);
    }

    [Fact]
    public void LaterAnchorDefinitionWinsOverAnEarlierFirstKey()
    {
        Assert.Equal("value", McsYamlReader.Parse("outer:\n  &k a: &k value\nnext: *k\n")["next"]);
    }

    [Fact]
    public void AnchorOnAQuotedFirstKeyBindsToThatKey()
    {
        Assert.Equal("inner", McsYamlReader.Parse("outer:\n  &k 'inner': value\nother: *k\n")["other"]);
    }

    [Theory]
    [InlineData("a: [foo:]\n")]
    [InlineData("a: [x, foo:]\n")]
    public void ColonBeforeAClosingBracketStaysInTheScalar(string yaml)
    {
        Assert.Contains("foo:", Assert.IsType<List<object?>>(McsYamlReader.Parse(yaml)["a"]).Cast<object?>());
    }

    [Fact]
    public void ColonBeforeAClosingBraceStaysInTheScalar()
    {
        Assert.Equal(new Dictionary<string, object?> { ["x"] = "foo:" }, McsYamlReader.Parse("a: {x: foo:}\n")["a"]);
    }

    [Fact]
    public void ColonBeforeAClosingBraceStaysInAFlowKey()
    {
        Assert.Equal(new Dictionary<string, object?> { ["foo:"] = null }, McsYamlReader.Parse("a: {foo:}\n")["a"]);
    }

    [Fact]
    public void ColonBeforeACommaSeparatesAFlowPair()
    {
        Assert.Equal(new List<object?> { new Dictionary<string, object?> { ["foo"] = null }, "bar" }, McsYamlReader.Parse("a: [foo:, bar]\n")["a"]);
    }

    [Theory]
    [InlineData("foo[bar]: x\n", "foo[bar]")]
    [InlineData("foo{bar}: x\n", "foo{bar}")]
    [InlineData("foo]bar[: x\n", "foo]bar[")]
    public void ReadsFirstKeysContainingFlowPunctuation(string yaml, string expectedKey)
    {
        Assert.Equal(new Dictionary<string, object?> { [expectedKey] = "x" }, McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("a: [!!str, x]\n")]
    [InlineData("a: [!!str]\n")]
    [InlineData("a: {!!str}\n")]
    public void ThrowsWhenATagIsNotFollowedByWhitespace(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void ReadsATagFollowedByAValueInAFlowSequence()
    {
        Assert.Equal(new List<object?> { "x" }, McsYamlReader.Parse("a: [!!str x]\n")["a"]);
    }

    [Theory]
    [InlineData("a: |\n    \n  text\n")]
    [InlineData("a: >\n    \n  text\n")]
    [InlineData("a: |\n   \n  text\n")]
    public void ThrowsWhenALeadingBlockScalarBlankLineIsIndentedPastTheContent(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("a: |\n\n  text\n", "\ntext\n")]
    [InlineData("a: |\n \n  text\n", "\ntext\n")]
    [InlineData("a: |\n  one\n    \n  two\n", "one\n  \ntwo\n")]
    public void KeepsBlockScalarBlankLinesThatDoNotOutdentTheContent(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: !!int 0x10\n", "16")]
    [InlineData("a: !!int 1_000\n", "1000")]
    [InlineData("a: !!int -5\n", "-5")]
    [InlineData("a: !!bool yes\n", "True")]
    [InlineData("a: !!bool off\n", "False")]
    [InlineData("a: !!float 1.0\n", "1")]
    [InlineData("a: !!float 1.5\n", "1.5")]
    public void ResolvesStandardScalarTags(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]!.ToString());
    }

    [Theory]
    [InlineData("a: !!str 0x10\n", "0x10")]
    [InlineData("a: !!str 'null'\n", "null")]
    [InlineData("a: !!str 1\n", "1")]
    public void KeepsStringTaggedScalarsAsText(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: !!int 0x10\n", "!!int", "0x10")]
    [InlineData("a: !!bool yes\n", "!!bool", "yes")]
    public void KeepsTheOriginalTagAndTextForTypedScalars(string yaml, string expectedTag, string expectedText)
    {
        var tagged = Assert.IsType<McsYamlTaggedScalar>(McsYamlReader.Parse(yaml)["a"]);

        Assert.Equal(expectedTag, tagged.Tag);
        Assert.Equal(expectedText, tagged.Text);
    }

    [Theory]
    [InlineData("a: !!int 0x10\n")]
    [InlineData("a: !!bool yes\n")]
    [InlineData("a: !!float 1.5\n")]
    public void RewritesTypedTaggedScalarsWithTheirTag(string yaml)
    {
        Assert.Equal(LineEndings.ToPlatform(yaml.Replace("\n", "\r\n")), McsYamlWriter.Write(McsYamlReader.Parse(yaml)));
    }

    [Theory]
    [InlineData("a: !!str plain\n", "a: plain\r\n")]
    [InlineData("a: !!str 'null'\n", "a: 'null'\r\n")]
    [InlineData("a: !!str 1\n", "a: 1\r\n")]
    public void RewritesStringTaggedScalarsAsPlainText(string yaml, string expected)
    {
        var rewritten = McsYamlWriter.Write(McsYamlReader.Parse(yaml));

        Assert.Equal(LineEndings.ToPlatform(expected), rewritten);
        Assert.Equal(McsYamlReader.Parse(yaml)["a"], McsYamlReader.Parse(rewritten)["a"]);
    }

    [Theory]
    [InlineData("a: [#comment\n one]\n")]
    [InlineData("a: [ #comment\n one]\n")]
    public void ReadsACommentRightAfterAFlowSequenceOpener(string yaml)
    {
        Assert.Equal(new List<object?> { "one" }, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void ReadsACommentRightAfterAFlowMappingOpener()
    {
        Assert.Equal(new Dictionary<string, object?> { ["foo"] = "bar" }, McsYamlReader.Parse("a: {#comment\n foo: bar}\n")["a"]);
    }

    [Theory]
    [InlineData("a: b#notacomment\n", "b#notacomment")]
    [InlineData("a: [x#notacomment]\n", null)]
    public void KeepsAHashInsideAScalar(string yaml, string? expected)
    {
        var value = McsYamlReader.Parse(yaml)["a"];

        Assert.Equal(expected ?? "x#notacomment", expected == null ? Assert.IsType<List<object?>>(value).Single() : value);
    }

    [Theory]
    [InlineData("a: [- foo]\n")]
    [InlineData("a: ? foo\n")]
    [InlineData("a: : foo\n")]
    public void ThrowsWhenAnIndicatorStartsAPlainScalar(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void KeepsExplicitKeysInFlowCollections()
    {
        Assert.Equal(new List<object?> { new Dictionary<string, object?> { ["foo"] = null }, "bar" }, McsYamlReader.Parse("a: [? foo, bar]\n")["a"]);
    }

    [Theory]
    [InlineData("  k: {x: 1,\n y: 2}\n")]
    [InlineData("  k: [a,\n b]\n")]
    [InlineData("  k: [a,\nb]\n")]
    public void ThrowsWhenAFlowCollectionContinuesAtOrBeforeTheParentIndent(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("  k: {x: 1,\n   y: 2}\n")]
    [InlineData("k: {x: 1,\n y: 2}\n")]
    [InlineData("outer:\n  k: [a,\n    b]\n")]
    public void ReadsFlowCollectionsThatStayIndentedPastTheParent(string yaml)
    {
        Assert.NotEmpty(McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("a: foo\n  - bar\n", "foo - bar")]
    [InlineData("a: -5\n", "-5")]
    [InlineData("a: ?query\n", "?query")]
    public void KeepsIndicatorsThatDoNotStartANode(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: &x &y foo\n")]
    [InlineData("a: &x\n  &y foo\n")]
    [InlineData("a: !!str !!int 5\n")]
    public void ThrowsWhenANodeHasMoreThanOneProperty(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void AllowsTheSameAnchorNameOnDifferentNodes()
    {
        Assert.Equal(new Dictionary<string, object?> { ["a"] = "one", ["b"] = "two", ["c"] = "two" }, McsYamlReader.Parse("a: &x one\nb: &x two\nc: *x\n"));
    }

    [Fact]
    public void AllowsAnAnchorAndATagOnTheSameNode()
    {
        Assert.Equal("5", McsYamlReader.Parse("a: &x !!int 5\nb: *x\n")["b"]!.ToString());
    }

    [Theory]
    [InlineData("a: !!int notanumber\n")]
    [InlineData("a: !!bool maybe\n")]
    [InlineData("a: !!float abc\n")]
    public void ThrowsWhenATaggedScalarDoesNotMatchItsTag(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void ResolvesTagsOnQuotedAndFlowScalars()
    {
        Assert.Equal("16", McsYamlReader.Parse("a: !!int \"0x10\"\n")["a"]!.ToString());
        Assert.Equal("3", Assert.IsType<List<object?>>(McsYamlReader.Parse("a: [!!int 3]\n")["a"]).Single()!.ToString());
    }

    [Theory]
    [InlineData("a: &n\n&k b: c\n")]
    [InlineData("a: &n\n!!str b: c\n")]
    public void PropertiesOnTheNextLineDoNotConsumeASibling(string yaml)
    {
        var result = McsYamlReader.Parse(yaml);

        Assert.Null(result["a"]);
        Assert.Equal("c", result["b"]);
    }

    [Fact]
    public void PropertiesOnTheNextLineLeaveSiblingAnchorsIntact()
    {
        var result = McsYamlReader.Parse("a: &n\n&k b: c\nd: *n\ne: *k\n");

        Assert.Null(result["d"]);
        Assert.Equal("b", result["e"]);
    }

    [Fact]
    public void PropertiesSpanningLinesStillApplyToIndentedContent()
    {
        Assert.Equal("value", McsYamlReader.Parse("a: &n\n  !!str value\nb: *n\n")["b"]);
    }

    [Fact]
    public void ResolvesAnAliasUsedAsTheFirstKeyOfABlockMapping()
    {
        Assert.Equal(new Dictionary<string, object?> { ["name"] = "value" }, McsYamlReader.Parse("key: &x name\nmap:\n  *x : value\n")["map"]);
    }

    [Fact]
    public void ResolvesAnAliasUsedAsTheFirstKeyOfASequenceItem()
    {
        Assert.Equal(new List<object?> { new Dictionary<string, object?> { ["name"] = "value" } }, McsYamlReader.Parse("key: &x name\nlist:\n- *x : value\n")["list"]);
    }

    [Theory]
    [InlineData("a: [&n null, *n]\n")]
    [InlineData("a: [&n ~, *n]\n")]
    public void AnchoredNullInAFlowSequenceResolvesToNull(string yaml)
    {
        Assert.Equal(new List<object?> { null, null }, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void AnchoredNullInAFlowSequenceResolvesToNullOutsideTheSequence()
    {
        Assert.Null(McsYamlReader.Parse("a: [&n null]\nb: *n\n")["b"]);
    }

    [Fact]
    public void AnchoredFlowKeyKeepsItsTextWhenUsedAsAKey()
    {
        Assert.Equal("null", McsYamlReader.Parse("a: {&n null: x}\nb: *n\n")["b"]);
    }

    [Fact]
    public void ReadsFlowCollectionsSeparatedByTabs()
    {
        Assert.Equal(new List<object?> { "value", "value" }, McsYamlReader.Parse("a: [&n\n\tvalue, *n]\n")["a"]);
    }

    [Theory]
    [InlineData("a: x\u2028  y\n", "x\u2028y")]
    [InlineData("a: x\u2029  y\n", "x\u2029y")]
    [InlineData("a: x\u2028\u2028  y\n", "x\u2028\u2028y")]
    [InlineData("a: x\n\u2028  y\n", "x\u2028y")]
    [InlineData("a: x\u2028\n  y\n", "x\u2028\ny")]
    public void PlainScalarsKeepLineAndParagraphSeparators(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: >-\n  x\u2028  y\n", "x\u2028y")]
    [InlineData("a: >-\n  x\n\u2028  y\n", "x\u2028y")]
    [InlineData("a: >-\n  x\u2028\n  y\n", "x\u2028\ny")]
    [InlineData("a: >-\n  x\n\n  y\n", "x\ny")]
    public void FoldedScalarsKeepLineSeparators(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: |-\n  x\u2028  y\n", "x\u2028y")]
    [InlineData("a: |-\n  x\u2028", "x")]
    [InlineData("a: |\n  x\u2028\u2028", "x\u2028")]
    [InlineData("a: |+\n  x\u2028", "x\u2028")]
    public void LiteralScalarsChompLineSeparators(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Theory]
    [InlineData("a: 'x\u2028\n  y'\n", "x\u2028\ny")]
    [InlineData("a: 'x\n\u2028  y'\n", "x\u2028y")]
    [InlineData("a: 'x\u2028  y'\n", "x\u2028y")]
    public void QuotedScalarsKeepLineSeparators(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void AnchorAllowsAnIndentlessSequenceValue()
    {
        var result = McsYamlReader.Parse("a: &x\n- one\nb: *x\n");

        Assert.Equal(new List<object?> { "one" }, result["a"]);
        Assert.Equal(new List<object?> { "one" }, result["b"]);
    }

    [Fact]
    public void AnchorWithoutContentInsideAFlowMappingIsNull()
    {
        Assert.Equal(new Dictionary<string, object?> { ["x"] = null, ["y"] = null }, McsYamlReader.Parse("a: {x: &n , y: *n}\n")["a"]);
    }

    [Fact]
    public void TagWithoutContentIsNull()
    {
        Assert.Null(McsYamlReader.Parse("a: !!str\n")["a"]);
    }

    [Theory]
    [InlineData("a: [one,,two]\n")]
    [InlineData("a: [,]\n")]
    [InlineData("a: {x: 1,,y: 2}\n")]
    public void ThrowsOnEmptyFlowEntries(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void AnchorOnItsOwnLineKeepsTheFollowingBlockMapping()
    {
        var result = McsYamlReader.Parse("a: &x\n  b: c\nd: *x\n");

        Assert.Equal(new Dictionary<string, object?> { ["b"] = "c" }, result["a"]);
        Assert.Equal(new Dictionary<string, object?> { ["b"] = "c" }, result["d"]);
    }

    [Fact]
    public void AnchorWithoutContentProducesNullSequenceItems()
    {
        Assert.Equal(new List<object?> { null, null }, McsYamlReader.Parse("a:\n- &x\n- *x\n")["a"]);
    }

    [Fact]
    public void FoldedScalarKeepsBlankLineBeforeMoreIndentedText()
    {
        Assert.Equal("one\n\n  two\n", McsYamlReader.Parse("a: >\n  one\n\n    two\n")["a"]);
    }

    [Fact]
    public void LiteralScalarKeepsLeadingBlankLine()
    {
        Assert.Equal("\none\n", McsYamlReader.Parse("a: |\n \n one\n")["a"]);
    }

    [Fact]
    public void LiteralScalarNormalizesNextLineToLineFeed()
    {
        Assert.Equal("one\ntwo\n", McsYamlReader.Parse("a: |\n  one\u0085  two\n")["a"]);
    }

    [Fact]
    public void FlowPlainScalarFoldsAcrossLines()
    {
        Assert.Equal(new List<object?> { "one two" }, McsYamlReader.Parse("a: [one\n two]\n")["a"]);
    }

    [Fact]
    public void PlainScalarContinuesOnAMoreIndentedDashLine()
    {
        Assert.Equal("one - two", McsYamlReader.Parse("a: one\n  - two\n")["a"]);
    }

    [Theory]
    [InlineData("a: \"one\\\ntwo\"\n", "onetwo")]
    [InlineData("a: \"one \\\ntwo\"\n", "one two")]
    [InlineData("a: \"one\\ \ntwo\"\n", "one  two")]
    public void DoubleQuotedScalarsFoldEscapedLineBreaks(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["a"]);
    }

    [Fact]
    public void ResolvesAnAliasUsedAsAKey()
    {
        Assert.Equal(new Dictionary<string, object?> { ["a"] = "b", ["b"] = "c" }, McsYamlReader.Parse("a: &k b\n*k : c\n"));
    }

    [Fact]
    public void ReadsAnExplicitBlockKey()
    {
        Assert.Equal(new Dictionary<string, object?> { ["a"] = "b" }, McsYamlReader.Parse("? a\n: b\n"));
    }

    [Fact]
    public void ReadsAnExplicitBlockKeyWithoutAValue()
    {
        Assert.Equal(new Dictionary<string, object?> { ["a"] = null }, McsYamlReader.Parse("? a\n"));
    }

    [Fact]
    public void ReadsAnExplicitFlowKey()
    {
        Assert.Equal(new Dictionary<string, object?> { ["one"] = "two" }, McsYamlReader.Parse("a: {? one: two}\n")["a"]);
    }

    [Fact]
    public void KeepsNullLiteralSpellingInKeyPosition()
    {
        Assert.Equal(new Dictionary<string, object?> { ["null"] = "a" }, McsYamlReader.Parse("null: a\n"));
    }

    [Theory]
    [InlineData("{[a]: one}\n")]
    [InlineData("a: {[b]: one}\n")]
    public void ThrowsWhenAKeyIsNotText(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("a: !foo bar\n")]
    [InlineData("%YAML 2.0\n---\na: b\n")]
    [InlineData("a: - b\n")]
    [InlineData("a: ,x\n")]
    [InlineData("a: one\n\tmore\n")]
    [InlineData("a: \"one\n---\ntwo\"\n")]
    public void ThrowsOnConstructsYamlRejects(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("a: b\u0085c: d\n")]
    [InlineData("a: b\u2028c: d\n")]
    [InlineData("a: b\u2029c: d\n")]
    public void TreatsUnicodeLineBreaksAsEntrySeparators(string yaml)
    {
        Assert.Equal(new Dictionary<string, object?> { ["a"] = "b", ["c"] = "d" }, McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void AliasOccurrenceCarriesItsOwnSourceSpan()
    {
        var positions = McsYamlReader.ParseDocument("a: &x foo\nb: *x\n").NodesInDocumentOrder().Skip(1).Select(node => (node.Start.Line, node.Start.Column)).ToList();

        Assert.Equal(new[] { (1, 1), (1, 7), (2, 1), (2, 4) }, positions);
    }

    [Fact]
    public void EmptyValueSpanStartsAfterItsKey()
    {
        var value = McsYamlReader.ParseDocument("a:   \nb: c\n").AllProperties().First().Value;

        Assert.Equal((1, 3), (value.Start.Line, value.Start.Column));
    }

    [Fact]
    public void ReadsManyEmptySequenceItemsWithoutOverflow()
    {
        Assert.Equal(5000, Assert.IsType<List<object?>>(McsYamlReader.Parse("a:\n" + string.Concat(Enumerable.Repeat("-\n", 5000)))["a"]).Count);
    }

    [Theory]
    [InlineData("[a, b]\n", "Sequence")]
    [InlineData("- a\n", "Sequence")]
    [InlineData("hello\n", "Scalar")]
    [InlineData("{\"name\":\"Connector\"}", "Mapping")]
    public void ParsesEveryRootShape(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.ParseDocument(yaml).Root.Kind.ToString());
    }

    [Fact]
    public void ParsesJsonFormattedDocuments()
    {
        var document = McsYamlReader.ParseDocument("{\n  \"connectorid\": \"a1\",\n  \"connectionparametersets\": null,\n  \"connectortype\": 1\n}");
        var value = Assert.IsType<Dictionary<string, object?>>(document.Root.ToValue());

        Assert.Equal("a1", value["connectorid"]);
        Assert.Null(value["connectionparametersets"]);
        Assert.Equal("1", value["connectortype"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# nothing\n")]
    [InlineData("   \n\n")]
    public void ReportsEmptyDocuments(string yaml)
    {
        Assert.True(McsYamlReader.ParseDocument(yaml).IsEmpty);
    }

    [Fact]
    public void ReportsLineAndColumnOnFailure()
    {
        var exception = Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse("a: 1\nb: 'unterminated\n"));

        Assert.Equal(2, exception.Line);
        Assert.Equal(4, exception.Column);
    }

    [Fact]
    public void ExposesPropertyPositions()
    {
        var identifier = McsYamlReader.ParseDocument("name: Flow\nid: abc\n").AllProperties().Single(property => property.Name == "id");

        Assert.Equal(2, identifier.NameStart.Line);
        Assert.Equal(1, identifier.NameStart.Column);
        Assert.Equal(3, identifier.NameEnd.Column);
        Assert.Equal(2, identifier.Value.Start.Line);
        Assert.Equal(5, identifier.Value.Start.Column);
        Assert.Equal("abc", identifier.Value.Scalar);
    }

    [Fact]
    public void ExposesValuePositionAfterExtraSeparationSpaces()
    {
        var property = McsYamlReader.ParseDocument("a:     value\n").AllProperties().Single();

        Assert.Equal(8, property.Value.Start.Column);
        Assert.Equal(13, property.Value.End.Column);
    }

    [Fact]
    public void ExposesNestedPropertyPositions()
    {
        var inner = McsYamlReader.ParseDocument("outer:\n  inner: value\n").AllProperties().Single(property => property.Name == "inner");

        Assert.Equal(2, inner.NameStart.Line);
        Assert.Equal(3, inner.NameStart.Column);
    }

    [Theory]
    [InlineData("name: Flow\nid: abc\n")]
    [InlineData("name: Flow\r\nid: abc\r\n")]
    public void PropertyIndexPointsAtOriginalText(string source)
    {
        Assert.Equal(source.IndexOf("id:", StringComparison.Ordinal), McsYamlReader.ParseDocument(source).AllProperties().Single(property => property.Name == "id").NameStart.Index);
    }

    [Fact]
    public void EnumeratesDuplicateKeysForValidation()
    {
        Assert.Equal(2, McsYamlReader.ParseDocument("items:\n- id: one\n- id: one\n").AllProperties().Count(property => property.Name == "id"));
    }

    [Fact]
    public void EnumeratesPropertiesInDocumentOrder()
    {
        var properties = McsYamlReader.ParseDocument("outer:\n  inner: value\nlast: end\n").AllProperties().ToList();

        Assert.Equal(new[] { "outer", "inner", "last" }, properties.Select(property => property.Name));
        Assert.Equal(new[] { 1, 2, 3 }, properties.Select(property => property.NameStart.Line));
    }

    [Fact]
    public void EnumeratesSequencePropertiesInDocumentOrder()
    {
        var properties = McsYamlReader.ParseDocument("items:\n- id: one\n  kind: A\n- id: two\n").AllProperties().ToList();

        Assert.Equal(new[] { "items", "id", "kind", "id" }, properties.Select(property => property.Name));
        Assert.Equal(new[] { 1, 2, 3, 4 }, properties.Select(property => property.NameStart.Line));
    }

    [Fact]
    public void EnumeratesNodesInDocumentOrder()
    {
        Assert.Equal(new[] { null, "kind", "Topic", "id", "abc" }, McsYamlReader.ParseDocument("kind: Topic\nid: abc\n").NodesInDocumentOrder().Select(node => node.Scalar).ToList());
    }

    [Fact]
    public void EnumeratesNestedNodesInDocumentOrder()
    {
        Assert.Equal(new[] { null, "outer", null, "inner", "value", "last", "end" }, McsYamlReader.ParseDocument("outer:\n  inner: value\nlast: end\n").NodesInDocumentOrder().Select(node => node.Scalar).ToList());
    }

    [Fact]
    public void SequenceValueYieldsContainerNodeBeforeItems()
    {
        Assert.Equal(new[] { null, "kind", null, "Topic" }, McsYamlReader.ParseDocument("kind: [Topic]\n").NodesInDocumentOrder().Select(node => node.Scalar).ToList());
    }

    [Fact]
    public void NodeOrderTracksAscendingIndex()
    {
        var indexes = McsYamlReader.ParseDocument("kind: Topic\nid: abc\n").NodesInDocumentOrder().Select(node => node.Start.Index).ToList();

        Assert.Equal(indexes.OrderBy(value => value), indexes);
    }
    [Theory]
    [InlineData("description: foo[#bar\n", "foo[#bar")]
    [InlineData("description: foo{#bar\n", "foo{#bar")]
    [InlineData("description: foo#bar\n", "foo#bar")]
    [InlineData("description: foo #bar\n", "foo")]
    public void HashAdjacentToFlowOpenerInsidePlainScalarIsNotAComment(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["description"]);
    }

    [Fact]
    public void HashAdjacentToFlowOpenerInsideKeyIsNotAComment()
    {
        Assert.Equal("value", McsYamlReader.Parse("foo[#bar: value\n")["foo[#bar"]);
    }

    [Theory]
    [InlineData("a: [#comment\n one]\n")]
    [InlineData("a: [ #comment\n one]\n")]
    public void CommentImmediatelyAfterFlowSequenceOpenerIsIgnored(string yaml)
    {
        Assert.Equal("one", Assert.IsType<List<object?>>(McsYamlReader.Parse(yaml)["a"]).Single());
    }

    [Fact]
    public void CommentImmediatelyAfterFlowMappingOpenerIsIgnored()
    {
        Assert.Equal("bar", Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("a: {#comment\n foo: bar}\n")["a"])["foo"]);
    }

    [Theory]
    [InlineData("a: [\n  'first'\n]\n")]
    [InlineData("a: [first,\n]\n")]
    [InlineData("a: [\n  first\n]\n")]
    public void FlowSequenceMayCloseAtOrBeforeParentIndent(string yaml)
    {
        Assert.Equal("first", Assert.IsType<List<object?>>(McsYamlReader.Parse(yaml)["a"]).Single());
    }

    [Fact]
    public void EmptyFlowSequenceMayCloseOnItsOwnLine()
    {
        Assert.Empty(Assert.IsType<List<object?>>(McsYamlReader.Parse("a: [\n]\n")["a"]));
    }

    [Fact]
    public void FlowMappingMayCloseAtOrBeforeParentIndent()
    {
        Assert.Equal("v", Assert.IsType<Dictionary<string, object?>>(McsYamlReader.Parse("a: {\n  k: v\n}\n")["a"])["k"]);
    }

    [Theory]
    [InlineData("a: [first\nsecond]\n")]
    [InlineData("a: [first,\nsecond]\n")]
    [InlineData("  k: {x: 1,\n y: 2}\n")]
    [InlineData("  k: {x: 1,\n  y: 2}\n")]
    [InlineData("p:\n  k: [a,\n  b]\n")]
    public void FlowContentDedentedToParentIndentIsRejected(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("  k: {x: 1,\n  'y': 2}\n")]
    [InlineData("  k: {x: 1,\n  \"y\": 2}\n")]
    [InlineData("  k: [1,\n  {y: 2}]\n")]
    [InlineData("  k: [1,\n  [2]]\n")]
    [InlineData("k: [a,\n!!int 5]\n")]
    [InlineData("k: [a,\n&x 5]\n")]
    public void FlowContinuationIndentAppliesOnlyToPlainScalars(string yaml)
    {
        Assert.NotNull(McsYamlReader.Parse(yaml));
    }

    [Fact]
    public void FlowPlainScalarMayContinueWhenIndented()
    {
        Assert.Equal("first second", Assert.IsType<List<object?>>(McsYamlReader.Parse("a: [first\n  second]\n")["a"]).Single());
    }

    [Theory]
    [InlineData("name: !!int 9223372036854775808\n")]
    [InlineData("name: !!int -9223372036854775809\n")]
    [InlineData("name: !!int 99999999999999999999999999999999\n")]
    public void TaggedIntegerOutsideSixtyFourBitRangeReportsFormatError(string yaml)
    {
        Assert.Throws<McsYamlFormatException>(() => McsYamlReader.Parse(yaml));
    }

    [Theory]
    [InlineData("name: !!int 9223372036854775807\n", "9223372036854775807")]
    [InlineData("name: !!int -9223372036854775808\n", "-9223372036854775808")]
    public void TaggedIntegerAtSixtyFourBitBoundaryIsAccepted(string yaml, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(yaml)["name"]!.ToString());
    }
}