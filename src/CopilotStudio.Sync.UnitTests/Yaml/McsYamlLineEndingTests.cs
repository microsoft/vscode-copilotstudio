// Copyright (C) Microsoft Corporation. All rights reserved.

using Microsoft.CopilotStudio.McsCore.Yaml;
using Microsoft.CopilotStudio.Sync.Dataverse;
using Xunit;

namespace Microsoft.CopilotStudio.Sync.UnitTests.Yaml;

public class McsYamlLineEndingTests
{
    [Theory]
    [MemberData(nameof(Documents))]
    public void ReaderProducesIdenticalValuesForEveryLineEndingStyle(string canonical)
    {
        var fromLf = McsYamlReader.Parse(LineEndings.ToLf(canonical));

        Assert.Equal(fromLf, McsYamlReader.Parse(LineEndings.ToCrLf(canonical)));
        Assert.Equal(fromLf, McsYamlReader.Parse(LineEndings.ToCr(canonical)));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void ReaderProducesIdenticalPositionsForEveryLineEndingStyle(string canonical)
    {
        var fromLf = DescribePositions(LineEndings.ToLf(canonical));

        Assert.Equal(fromLf, DescribePositions(LineEndings.ToCrLf(canonical)));
        Assert.Equal(fromLf, DescribePositions(LineEndings.ToCr(canonical)));
    }

    [Theory]
    [InlineData("description: |\r\n  first\r\n  second\r\n", "first\nsecond\n")]
    [InlineData("description: |-\r\n  first\r\n  second\r\n", "first\nsecond")]
    [InlineData("description: >\r\n  first\r\n\r\n  second\r\n", "first\nsecond\n")]
    [InlineData("description: \"first\r\n  second\"\r\n", "first second")]
    public void ReaderNormalizesLineBreaksInsideScalarsToLineFeed(string canonical, string expected)
    {
        Assert.Equal(expected, McsYamlReader.Parse(LineEndings.ToLf(canonical))["description"]);
        Assert.Equal(expected, McsYamlReader.Parse(LineEndings.ToCrLf(canonical))["description"]);
        Assert.Equal(expected, McsYamlReader.Parse(LineEndings.ToCr(canonical))["description"]);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void WriterEmitsOnlyPlatformLineEndings(string canonical)
    {
        var rewritten = McsYamlWriter.Write(McsYamlReader.Parse(canonical));

        Assert.True(LineEndings.ArePlatformNative(rewritten), $"expected platform line endings but found: {Describe(rewritten)}");
    }

    [Theory]
    [MemberData(nameof(CanonicalDocuments))]
    public void RewritingIsByteIdenticalWhenTheSourceUsesPlatformLineEndings(string canonical)
    {
        var source = LineEndings.ToPlatform(canonical);

        Assert.Equal(source, McsYamlWriter.Write(McsYamlReader.Parse(source)));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void RewritingPreservesContentWhateverLineEndingsTheSourceUsed(string canonical)
    {
        var expected = LineEndings.ToLf(McsYamlWriter.Write(McsYamlReader.Parse(LineEndings.ToPlatform(canonical))));

        Assert.Equal(expected, LineEndings.ToLf(McsYamlWriter.Write(McsYamlReader.Parse(LineEndings.ToLf(canonical)))));
        Assert.Equal(expected, LineEndings.ToLf(McsYamlWriter.Write(McsYamlReader.Parse(LineEndings.ToCrLf(canonical)))));
        Assert.Equal(expected, LineEndings.ToLf(McsYamlWriter.Write(McsYamlReader.Parse(LineEndings.ToCr(canonical)))));
    }

    [Fact]
    public void WriterUsesTheLineEndingOfTheHostPlatform()
    {
        Assert.Equal($"a: 1{Environment.NewLine}b: 2{Environment.NewLine}", McsYamlWriter.Write(new Dictionary<string, object?> { ["a"] = "1", ["b"] = "2" }));
    }

    [Fact]
    public void ObjectMapperSerializeUsesPlatformLineEndings()
    {
        var yaml = McsYamlObjectMapper.Serialize(new SyncDataverseClient.AIPromptMetadata { Name = "prompt" });

        Assert.True(LineEndings.ArePlatformNative(yaml), $"expected platform line endings but found: {Describe(yaml)}");
        Assert.Contains($"name: prompt{Environment.NewLine}", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void ObjectMapperReadsDocumentsWrittenOnAnyPlatform(string canonical)
    {
        var fromLf = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(LineEndings.ToLf(canonical));
        var fromCrLf = McsYamlObjectMapper.Deserialize<SyncDataverseClient.WorkflowMetadata>(LineEndings.ToCrLf(canonical));

        Assert.Equal(fromLf?.Name, fromCrLf?.Name);
        Assert.Equal(fromLf?.StateCode, fromCrLf?.StateCode);
        Assert.Equal(fromLf?.ConnectionReferences, fromCrLf?.ConnectionReferences);
    }

    [Theory]
    [InlineData("name: Flow\r\nstateCode: 0\r\n", "name: Flow\nstateCode: 0\n")]
    [InlineData("connectionReferences:\r\n- shared_a\r\n", "connectionReferences:\n- shared_a\n")]
    public void ComparerTreatsLineEndingDifferencesAsEqual(string left, string right)
    {
        Assert.True(McsYamlComparer.DocumentsMatch(left, right));
        Assert.True(McsYamlComparer.DocumentsMatch(LineEndings.ToCr(left), right));
    }

    private static string DescribePositions(string yaml)
    {
        var document = McsYamlReader.ParseDocument(yaml);

        return string.Join(
            " ",
            document.AllProperties().Select(property =>
                $"{property.Name}@{property.NameStart.Line}:{property.NameStart.Column}->{property.NameEnd.Line}:{property.NameEnd.Column}" +
                $"={property.Value.Start.Line}:{property.Value.Start.Column}"));
    }

    private static string Describe(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");

    public static TheoryData<string> Documents()
    {
        var documents = CanonicalDocuments();
        documents.Add("# header\r\nname: Flow\r\n");
        documents.Add("name: Flow\r\n\r\n\r\nstateCode: 0\r\n");
        return documents;
    }

    public static TheoryData<string> CanonicalDocuments() => new()
    {
        "name: Agent Flow 1\r\nstateCode: 1\r\n",
        "config:\r\n  inner:\r\n    leaf: value\r\n",
        "connectionReferences:\r\n- shared_a\r\n- shared_b\r\n",
        "name: Structured\r\ntriggers:\r\n- kind: Http\r\n  method: POST\r\n- kind: Timer\r\n",
        "description: \r\nname: Flow\r\n",
        "connectionReferences: []\r\n",
        "name: 'Name: with colon'\r\ndescription: ''\r\n",
        "name: Flow é中文\r\ndescription: Ünïcödé\r\n",
        "isCustomizable:\r\n  value: true\r\n  canBeChanged: false\r\n",
        "name: \"escaped\\nnewline\"\r\n",
    };
}
